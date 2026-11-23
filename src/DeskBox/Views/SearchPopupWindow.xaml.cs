using DeskBox.Controls;
using DeskBox.Controls.Search;
using DeskBox.Features.Search;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using WinRT;

namespace DeskBox.Views;

/// <summary>
/// Spotlight-style search popup window.
/// Appears centered horizontally, 1/3 from the top of the primary display.
/// Layout: search box, dynamic tab bar, result list, and a persistent footer.
/// The surface material and border follow the widget appearance settings.
/// </summary>
public sealed partial class SearchPopupWindow : Window, ISearchRecommendationGridHost, ISearchRecommendationOverlayHost
{
    private readonly SearchPopupViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly SpacePreviewBroker _spacePreviewService = new();
    private readonly ThemeService? _themeService;
    private DispatcherTimer? _searchDebounceTimer;
    private IDisposable? _morphAnimationRegistration;
    private long _heightSpringLastTimestamp;
    private double _heightSpringCurrentLogical;
    private double _heightSpringVelocity;
    private bool _heightSpringExpanding;
    private double _heightMorphTargetLogical;
    private double _widthSpringCurrentLogical;
    private double _widthSpringVelocity;
    private bool _widthSpringExpanding;
    private double _widthMorphTargetLogical;
    private RectInt32 _heightMorphPendingBounds;
    private RectInt32 _morphWorkAreaPhysical;
    private double _morphDpiScale = 1.0;
    private int? _morphCustomX;
    private int? _morphCustomY;
    private int _lastMorphWindowX = int.MinValue;
    private bool _suppressPanelEntranceAnimation;
    private bool _hasPlayedOpenRecommendationsEntrance;

    private bool IsMorphSpringRunning => _morphAnimationRegistration is not null;

    // When non-null, the recommended-apps entrance animation is "pending": each
    // card is animated the moment ItemsRepeater realizes it (via ElementPrepared),
    // because TryGetElement returns null until a layout pass has realized the
    // virtualized elements. The flag carries the configured style index.
    private DispatcherQueueTimer? _coldHiddenCleanupTimer;
    private DispatcherQueueTimer? _unfocusedAutoHideTimer;
    private bool _isPopupInputActive;
    private bool _pendingInitialSearchBoxFocus;
    private AppWindow? _appWindow;
    private IntPtr _hwnd;

    // Native backdrop controllers (same approach as desktop widgets).
    private MicaController? _micaController;
    private DesktopAcrylicController? _acrylicController;
    private SolidTintSystemBackdrop? _transparentWindowBackdrop;
    private ElementBackdropTargetBridge? _materialBackdropBridge;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private ICompositionSupportsSystemBackdrop? _backdropTarget;
    private bool _micaControllerAttached;
    private bool _acrylicControllerAttached;

    private const int MinPopupWidth = SearchPopupSurfacePolicy.MinPopupWidthLogical;

    // Close-path forensics: DeskBox never closes this window on its own except via
    // an explicit service dispose (which logs "[Search] Services disposed"), so a
    // WM_CLOSE here means an external sender or a framework teardown. The next
    // diagnostics export can then distinguish "hidden by managed code" (Popup
    // hidden), "closed via WM_CLOSE" (external WM_CLOSE), and "destroyed without
    // WM_CLOSE" (WinUI/DWM teardown).
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmDpiChanged = 0x02E0;
    private static readonly UIntPtr PopupCloseWatcherSubclassId = new(0x5E4C);
    private static App? CurrentApp => App.Current as App;
    private readonly Win32Helper.SubclassProc _popupCloseWatcherProc;
    private bool _isPopupCloseWatcherInstalled;
    private bool _isWindowClosed;

    // The popup uses the same pointer-capture interaction model as widget windows,
    // avoiding the visible native WS_THICKFRAME border.
    private FrameworkElement? _windowInteractionElement;
    private bool _isWindowDragging;
    private bool _isWindowResizing;
    private string _resizeDirection = string.Empty;
    private Win32Helper.POINT _interactionStartCursor;
    private RectInt32 _interactionStartBounds;

    // Keyboard-selected result row. Hover feedback is owned by the row control itself.
    private SearchResultRowControl? _selectedRow;
    private bool _isNavigatingResults;
    private double? _measuredResultRowHeight;
    private int _selectedRowHighlightGeneration;
    private int _tabFocusRestoreGeneration;

    // One stable search surface. Width is user-resizable horizontally and shared
    // across all popup states; height remains strictly content-adaptive.
    private const int PopupWidth = SearchPopupSurfacePolicy.DefaultPopupWidthLogical;
    private const int PopupHeight = 500;

    public SearchPopupWindow(
        SearchPopupViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        _viewModel = viewModel;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _themeService = (App.Current as App)?.ThemeService;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _viewModel.OwnerWindowHandle = _hwnd;
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        RecommendedAppsGrid.Initialize(_viewModel, _settingsService, _localizationService, this);
        RecommendationOverlays.Initialize(_viewModel, _localizationService, this);

        ConfigureWindow();
        ApplyTheme();
        SetupBindings();

        _viewModel.ActionRequested += OnViewModelActionRequested;
        _viewModel.ContentRequested += OnViewModelContentRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.CurrentResults.CollectionChanged += OnCurrentResultsCollectionChanged;
        ResultsRepeater.ElementPrepared += OnResultsElementPrepared;
        ResultsRepeater.ElementClearing += OnResultsElementClearing;
        _viewModel.RecommendationIconsResolved += OnRecommendationIconsResolved;
        ApplyWin10SafeSearchGlyphs();
        _viewModel.RecommendationLayoutUpdated += OnRecommendationLayoutUpdated;
        _settingsService.SettingsChanged += OnAppearanceSettingsChanged;
        _settingsService.AppearancePreviewChanged += OnAppearanceSettingsChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        if (_themeService is not null)
            _themeService.AppearanceChanged += OnThemeServiceAppearanceChanged;
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
        _popupCloseWatcherProc = CallbackDepth.WrapSubclassProc(PopupCloseWatcherSubclassProc);
        _isPopupCloseWatcherInstalled = Win32Helper.SetWindowSubclass(
            _hwnd,
            _popupCloseWatcherProc,
            PopupCloseWatcherSubclassId,
            UIntPtr.Zero);
        App.Log(
            $"[Search] Popup close watcher installed hwnd=0x{_hwnd.ToInt64():X} " +
            $"subclass={_isPopupCloseWatcherInstalled}");
    }

    private IntPtr PopupCloseWatcherSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message == WmClose)
        {
            App.Log(
                $"[Search] Popup WM_CLOSE received hwnd=0x{hWnd.ToInt64():X} " +
                $"visible={IsPopupVisible}");
        }
        else if (message == WmDestroy)
        {
            App.Log($"[Search] Popup WM_DESTROY hwnd=0x{hWnd.ToInt64():X}");
            IntPtr destroyResult = Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
            RemovePopupCloseWatcherSubclass();
            return destroyResult;
        }
        else if (message == WmDpiChanged)
        {
            IntPtr dpiResult = Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
            if (IsPopupVisible && !_isWindowDragging && !_isWindowResizing)
            {
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (IsPopupVisible && !_isWindowClosed && !_isWindowDragging && !_isWindowResizing)
                    {
                        UpdatePopupDynamicBounds(forceWhenOpening: true);
                    }
                });
            }

            return dpiResult;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void RemovePopupCloseWatcherSubclass()
    {
        if (!_isPopupCloseWatcherInstalled || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _isPopupCloseWatcherInstalled = false;
        try
        {
            Win32Helper.RemoveWindowSubclass(
                _hwnd,
                _popupCloseWatcherProc,
                PopupCloseWatcherSubclassId);
        }
        catch
        {
        }
    }

    public IntPtr WindowHandle => _hwnd;
    public bool IsPopupVisible { get; private set; }

    /// <summary>
    /// True only while the popup is actively focused, hovered, or mid-gesture.
    /// When the user clicks another window and leaves the popup covered underneath,
    /// it stops blocking background/idle memory compression immediately.
    /// </summary>
    internal bool IsBlockingMemoryCleanup =>
        IsPopupVisible &&
        (_isPopupInputActive || HasActivePopupInteraction() || IsPointerOverPopupWindow());

    /// <summary>
    /// Raised when an action needs to be handled by the app (e.g., open settings).
    /// </summary>
    public event EventHandler<string>? ActionRequested;

    public event EventHandler<SearchResultItem>? ContentRequested;

    public event EventHandler? PopupShown;

    public event EventHandler? PopupHidden;

    public event EventHandler? PopupDeactivated;

    /// <summary>
    /// Shows the popup at the correct position and focuses the search box.
    /// </summary>
    public async void ShowPopup()
    {
        try { await ShowPopupCoreAsync(null); }
        catch (Exception ex) { App.Log($"[SearchPopup] ShowPopup failed: {ex}"); }
    }

    /// <summary>
    /// Shows the popup with a pre-filled query and immediately executes the search.
    /// </summary>
    public async void ShowPopupWithQuery(string query)
    {
        try { await ShowPopupCoreAsync(query); }
        catch (Exception ex) { App.Log($"[SearchPopup] ShowPopupWithQuery failed: {ex}"); }
    }

    private async Task ShowPopupCoreAsync(string? initialQuery)
    {
        if (_isWindowClosed)
        {
            return;
        }

        CancelColdHiddenCleanupTimer();
        CancelUnfocusedAutoHideTimer();
        if (IsPopupVisible)
        {
            ActivateSearchInput();
            if (!string.IsNullOrWhiteSpace(initialQuery))
            {
                _latchedInlineScope = SearchInlineScope.None;
                _suppressScopeLatchTextChange = true;
                try
                {
                    SearchTextBox.Text = initialQuery;
                }
                finally
                {
                    _suppressScopeLatchTextChange = false;
                }
                TryLatchInlineScopeFromInput(SearchTextBox.Text);
                _viewModel.Query = GetEffectivePopupQuery();
                UpdatePanelVisibility();
            }

            return;
        }

        var showStopwatch = Stopwatch.StartNew();

        // Cancel any in-flight exit animation (and its pending window hide) so a fast
        // Alt+D re-toggle interrupts the dismissal instead of racing with it.
        PopupHideStoryboard.Stop();
        PopupHideStoryboard.Completed -= OnPopupHideCompleted;
        _hasPlayedOpenRecommendationsEntrance = false;

        // Appearance settings may have changed while the popup was hidden.
        ApplyMaterialFromSettings();

        // Sync initial query and dynamic capsule/anchored downward bounds before showing
        // so the first rendered frame already matches the exact capsule or recommendation height.
        _latchedInlineScope = SearchInlineScope.None;
        _suppressScopeLatchTextChange = true;
        try
        {
            SearchTextBox.Text = initialQuery ?? string.Empty;
        }
        finally
        {
            _suppressScopeLatchTextChange = false;
        }
        TryLatchInlineScopeFromInput(SearchTextBox.Text);
        UpdateScopePrefixBadge(SearchTextBox.Text);
        string effectiveInitialQuery = GetEffectivePopupQuery();
        if (!string.IsNullOrWhiteSpace(effectiveInitialQuery))
        {
            _viewModel.Query = effectiveInitialQuery;
        }
        UpdatePanelVisibility(forceBoundsWhenOpening: true);

        RootGrid.Opacity = AreSystemAnimationsEnabled() ? 0 : 1;
        PopupTranslateTransform.Y = 0;
        PopupScaleTransform.ScaleX = AreSystemAnimationsEnabled() ? 0.985 : 1;
        PopupScaleTransform.ScaleY = AreSystemAnimationsEnabled() ? 0.985 : 1;
        _appWindow?.Show();
        IsPopupVisible = true;
        _isPopupInputActive = true;
        PopupShown?.Invoke(this, EventArgs.Empty);

        // Bring the popup above all windows (including desktop-level widgets) at the
        // moment it is invoked, but do NOT keep it always-on-top. After this, normal
        // z-order rules apply: clicking another window will cover the popup.
        // During a quick-reveal raised session the widget group is held topmost,
        // so the manager routes this through the raised band instead of the
        // normal-band pulse, which would land the popup below the widgets.
        App.Current.WidgetManager?.BringAuxiliaryWindowToFront(
            _hwnd,
            "search-popup-shown");

        // This is an interactive search window, so it must be activatable again after
        // the user works in another app.
        _pendingInitialSearchBoxFocus = true;
        DeskBox.Helpers.TransientInputFocusGuard.ActivateWindowAndFocusIsland(this, _hwnd);

        // Fluent entrance: quick scale + fade + rise, matching the Windows 11
        // menu/popup transition language.
        if (AreSystemAnimationsEnabled())
        {
            PopupShowStoryboard.Begin();
        }

        // Focus immediately; on the very first show of a newly created Window,
        // SearchTextBox.IsLoaded may still be false so Focus() returns false —
        // _pendingInitialSearchBoxFocus ensures Loaded, DispatcherQueue, and the
        // post-yield continuation complete XAML + TSF/IME focus before typing.
        if (!TryFocusSearchTextBoxForInput())
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsPopupVisible && !_isWindowClosed && _pendingInitialSearchBoxFocus)
                {
                    _ = TryFocusSearchTextBoxForInput();
                }
            });
        }

        // Yield before recommendations, icons, or an idle-unloaded index do any work.
        // The native window can paint and accept input while those tasks continue.
        await Task.Yield();
        if (!IsPopupVisible || _isWindowClosed)
        {
            return;
        }

        if (_pendingInitialSearchBoxFocus)
        {
            if (Win32Helper.GetForegroundWindow() != _hwnd)
            {
                DeskBox.Helpers.TransientInputFocusGuard.ActivateWindowAndFocusIsland(this, _hwnd);
            }

            _ = TryFocusSearchTextBoxForInput();
        }

        showStopwatch.Stop();
        PerformanceLogger.Mark(
            "SearchPopupFirstFrameYield",
            $"elapsedMs={showStopwatch.ElapsedMilliseconds}");

        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            // When opened with a pre-filled query, skip the empty-state recommendations
            // and directly trigger the search.
            _viewModel.UpdateEverythingPromptState();
            _ = _viewModel.EnsureEverythingConnectionProbedAsync();
            UpdateEverythingPromptBanner();
            _viewModel.Query = initialQuery;
            UpdatePanelVisibility();
        }
        else
        {
            // Any complete cached set is good enough for the first frame. An expired
            // set refreshes atomically in the background instead of flashing empty.
            bool hadRecommendationCache = _viewModel.HasRecommendationCache;
            bool showSkeleton = _settingsService.Settings.SearchShowRecommendations
                && !hadRecommendationCache;
            if (showSkeleton)
            {
                StopHeightMorphTimer();
                RecommendedAppsGrid.StartSkeletonDelay();
            }

            // Recommendation identities may be published before their shell icons
            // finish resolving. Suppress property-change driven entrance playback
            // during this open transaction and play exactly once below.
            _suppressPanelEntranceAnimation = true;
            try
            {
                await _viewModel.OnPopupOpenedAsync();
            }
            finally
            {
                _suppressPanelEntranceAnimation = false;
            }

            if (!IsPopupVisible)
            {
                RecommendedAppsGrid.CancelSkeletonDelayAndHide();
                return;
            }

            if (showSkeleton)
            {
                RecommendedAppsGrid.CancelSkeletonDelayAndHide();
            }

            UpdatePanelVisibility();

            // Replaying the configured card entrance on every popup invocation is
            // intentional. A cached reopen should be immediate, but not visually
            // static; the window and its application cards are one entrance gesture.
            if (RecommendedAppsGrid.IsAppsPanelVisible)
            {
                _hasPlayedOpenRecommendationsEntrance = true;
                RecommendedAppsGrid.AnimateEntrance();
            }
        }
    }

    /// <summary>
    /// Focuses an already-visible popup without replaying its open pipeline or
    /// clearing the current query. This makes repeated desktop-widget clicks safe.
    /// </summary>
    public void ActivateSearchInput()
    {
        CancelColdHiddenCleanupTimer();
        CancelUnfocusedAutoHideTimer();
        _isPopupInputActive = true;
        PopupHideStoryboard.Stop();
        PopupHideStoryboard.Completed -= OnPopupHideCompleted;
        PopupShowStoryboard.Stop();
        RootGrid.Opacity = 1.0;
        PopupScaleTransform.ScaleX = 1.0;
        PopupScaleTransform.ScaleY = 1.0;
        _appWindow?.Show();
        App.Current.WidgetManager?.BringAuxiliaryWindowToFront(
            _hwnd,
            "search-popup-reactivated");
        _pendingInitialSearchBoxFocus = true;
        DeskBox.Helpers.TransientInputFocusGuard.ActivateWindowAndFocusIsland(this, _hwnd);
        if (!TryFocusSearchTextBoxForInput())
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsPopupVisible && !_isWindowClosed && _pendingInitialSearchBoxFocus)
                {
                    _ = TryFocusSearchTextBoxForInput();
                }
            });
        }
    }

    private bool TryFocusSearchTextBoxForInput()
    {
        if (_isWindowClosed || !IsPopupVisible)
        {
            return false;
        }

        DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(SearchTextBox);
        if (!SearchTextBox.IsLoaded)
        {
            _pendingInitialSearchBoxFocus = true;
            return false;
        }

        bool focused = SearchTextBox.Focus(FocusState.Programmatic) ||
                       SearchTextBox.FocusState != FocusState.Unfocused;
        _pendingInitialSearchBoxFocus = !focused;
        return focused;
    }

    /// <summary>
    /// Hides the popup without destroying it.
    /// </summary>
    public void HidePopup()
    {
        if (!IsPopupVisible)
        {
            return;
        }

        App.Log("[Search] Popup hidden");
        CancelUnfocusedAutoHideTimer();
        DisarmDragOutCandidate();
        StopExternalDragFadeTimer(restoreOpacity: false);
        StopHeightMorphTimer();
        CompleteDetailsPaneClose();
        _isPopupInputActive = false;
        _pendingInitialSearchBoxFocus = false;
        IsPopupVisible = false;
        _hasPlayedOpenRecommendationsEntrance = false;
        RecommendedAppsGrid.CancelDrag();
        RecommendedAppsGrid.DisableEditMode();
        RecommendationOverlays.CloseOverlays(immediate: true);
        if (_isCmdBarFlyoutOpen)
        {
            CmdBarActionsFlyout.Hide();
        }
        if (_isManageTabsFlyoutOpen)
        {
            ManageTabsFlyout.Hide();
        }
        _activeRightDropMenu?.Hide();
        _activeRightDropMenu = null;
        _detailsUpdateCoalesceTimer?.Stop();
        ResetSearchSessionVisualFlags();
        _displayedDetailsItem = null;
        ResetLatchedScopeState();
        ClearSelectedResultRow();
        RecommendedAppsGrid.ClearSelection();
        RecommendedAppsGrid.ClearHoveredCard();
        RecommendationOverlays.ClearHoveredCard();
        RecommendedAppsGrid.ResetDoubleClickTracking();
        ClearAllResultRowTransientStates();
        ClearMultiSelection();
        _viewModel.OnPopupHidden();
        _searchDebounceTimer?.Stop();
        RecommendedAppsGrid.CancelSkeletonDelayAndHide();
        SearchProgressBar.IsIndeterminate = false;
        LoadingRing.IsActive = false;
        SearchFeedbackPresenter.Clear();
        RecommendedAppsGrid.StopEntranceGuard();
        PopupHidden?.Invoke(this, EventArgs.Empty);

        // Fluent exit: fast shrink + fade, then remove the window from view once the
        // animation completes. Skip the From="1" storyboard when the popup was already
        // faded (e.g., auto-hiding at 0.20 opacity after an external drag-out drop) so
        // dismissal never flashes back to 100% opacity.
        double currentOpacity = RootGrid.Opacity;
        PopupShowStoryboard.Stop();
        PopupHideStoryboard.Completed -= OnPopupHideCompleted;
        if (AreSystemAnimationsEnabled() && currentOpacity >= 0.5)
        {
            PopupHideStoryboard.Completed += OnPopupHideCompleted;
            PopupHideStoryboard.Begin();
        }
        else
        {
            RootGrid.Opacity = 0;
            OnPopupHideCompleted(null, EventArgs.Empty);
        }
    }

    private void OnPopupHideCompleted(object? sender, object e)
    {
        PopupHideStoryboard.Completed -= OnPopupHideCompleted;

        // If the popup was re-shown while this callback was queued, the storyboard was
        // already stopped and this completion is stale; never hide a visible popup.
        if (!IsPopupVisible)
        {
            SearchProgressBar.IsIndeterminate = false;
            LoadingRing.IsActive = false;
            App.Current.WidgetManager?.RestoreForegroundToRaisedWidgetBeforeAuxiliaryHide(_hwnd);
            _appWindow?.Hide();
            // Leave the quick-reveal raised band at the actual hide moment, not at
            // the hide request: releasing earlier would sink the fading window
            // below the still-topmost widget group mid exit animation.
            App.Current.WidgetManager?.ReleaseRaisedBandGuest(
                _hwnd,
                "search-popup-hidden");
            // Dispose DWM backdrop controllers immediately while hidden, then close
            // the cold-hidden popup window after the retention delay to reclaim its
            // DXGI SwapChain.
            DisposeMicaController();
            DisposeAcrylicController();
            ClearTransparentWindowBackdrop();
            ArmColdHiddenCleanupTimer();
        }
    }

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

    private bool HasActivePopupInteraction() =>
        _isWindowDragging ||
        _isWindowResizing ||
        RecommendedAppsGrid.HasActiveInteraction ||
        RecommendationOverlays.HasActiveInteraction ||
        _isRubberBanding ||
                        _isExternalFileDragActive ||
        IsAnyPopupFlyoutOpen ||
        _dragOutCandidate is not null ||
        _dragOutInProgress ||
        (RootGrid.XamlRoot is { } xamlRoot &&
         VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Count > 0);

    private bool IsPointerOverPopupWindow()
    {
        if (_hwnd == IntPtr.Zero ||
            !Win32Helper.GetCursorPos(out var cursor) ||
            !Win32Helper.GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        if (cursor.X < rect.Left || cursor.X >= rect.Right ||
            cursor.Y < rect.Top || cursor.Y >= rect.Bottom)
        {
            return false;
        }

        IntPtr hit = Win32Helper.WindowFromPoint(cursor);
        if (hit == IntPtr.Zero)
        {
            return false;
        }

        IntPtr root = Win32Helper.GetAncestor(hit, Win32Helper.GA_ROOT);
        return (root != IntPtr.Zero ? root : hit) == _hwnd;
    }

    private void ArmUnfocusedAutoHideTimer()
    {
        if (!IsPopupVisible || _isWindowClosed)
        {
            CancelUnfocusedAutoHideTimer();
            return;
        }

        int releaseDelaySeconds = PerformanceSettingsPolicy
            .Resolve(_settingsService.Settings)
            .TransientWindowReleaseDelaySeconds;
        int autoHideSeconds = releaseDelaySeconds <= 0
            ? 60
            : Math.Clamp(releaseDelaySeconds, 60, 180);

        DispatcherQueue? queue = DispatcherQueue;
        if (queue is null)
        {
            return;
        }

        TimeSpan interval = TimeSpan.FromSeconds(autoHideSeconds);
        if (_unfocusedAutoHideTimer is null)
        {
            DispatcherQueueTimer timer = queue.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = interval;
            timer.Tick += UnfocusedAutoHideTimer_Tick;
            _unfocusedAutoHideTimer = timer;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }
        else
        {
            _unfocusedAutoHideTimer.Stop();
            _unfocusedAutoHideTimer.Interval = interval;
        }

        _unfocusedAutoHideTimer.Start();
    }

    private void CancelUnfocusedAutoHideTimer()
    {
        _unfocusedAutoHideTimer?.Stop();
    }

    private void DisposeUnfocusedAutoHideTimer()
    {
        if (_unfocusedAutoHideTimer is not { } timer)
        {
            return;
        }

        _unfocusedAutoHideTimer = null;
        timer.Tick -= UnfocusedAutoHideTimer_Tick;
        try
        {
            timer.Stop();
        }
        catch
        {
        }
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void UnfocusedAutoHideTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!IsPopupVisible || _isWindowClosed)
        {
            return;
        }

        if (_isPopupInputActive ||
            Win32Helper.GetForegroundWindow() == _hwnd ||
            HasActivePopupInteraction() ||
            IsPointerOverPopupWindow())
        {
            ArmUnfocusedAutoHideTimer();
            return;
        }

        App.Log("[Search] Unfocused popup auto-hiding after idle timeout");
        HidePopup();
    }

    private void ColdHiddenCleanupTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (IsPopupVisible || _isWindowClosed)
        {
            return;
        }

        RecommendedAppsGrid.DisableEditMode();
        ClearSelectedResultRow();
        RecommendedAppsGrid.ClearSelection();
        RecommendedAppsGrid.ClearHoveredCard();
        RecommendationOverlays.ClearHoveredCard();
        RecommendedAppsGrid.ResetDoubleClickTracking();
        ClearAllResultRowTransientStates();
        ClearMultiSelection();
        ClearRealizedRepeaterVisuals();
        int releasedIcons = _viewModel.ReleaseColdHiddenCaches();
        SearchEngineService.ReleaseActiveColdHiddenCaches();
        App.Log(
            $"[Search] Cold-hidden popup closing to reclaim swapchain icons={releasedIcons}");
        App.Current.EnsureXamlLoopLifelineIfNoOtherWindows();
        Close();
    }

    /// <summary>
    /// Toggles the popup visibility.
    /// </summary>
    public void TogglePopup()
    {
        if (IsPopupVisible)
        {
            HidePopup();
        }
        else
        {
            ShowPopup();
        }
    }

    private void ClearSelectedResultRow()
    {
        if (_selectedRow is not null)
        {
            _selectedRow.IsSelected = false;
            _selectedRow.IsTabStop = false;
            _selectedRow = null;
        }
    }

    private void ClearRealizedRepeaterVisuals()
    {
        int resultChildCount = VisualTreeHelper.GetChildrenCount(ResultsRepeater);
        for (int i = 0; i < resultChildCount; i++)
        {
            if (VisualTreeHelper.GetChild(ResultsRepeater, i) is SearchResultRowControl row)
            {
                row.IsSelected = false;
                row.IsMultiSelected = false;
                row.IsTabStop = false;
                row.PrepareItem(null);
                row.RefreshIconVisuals();
                row.DataContext = null;
            }
        }

        RecommendedAppsGrid.ClearRealizedVisuals();
        RecommendationOverlays.ClearRealizedVisuals();
    }

    private void ConfigureWindow()
    {
        if (_appWindow is null)
        {
            return;
        }

        // Remove title bar
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);

        _appWindow.Resize(new SizeInt32(PopupWidth, PopupHeight));

        // Keep the popup off the taskbar, but leave it activatable so text input and
        // pointer interaction recover normally after another app receives focus.
        int extendedStyle = Win32Helper.GetWindowLong(_hwnd, Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        extendedStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
        Win32Helper.SetWindowLongPtr(_hwnd, Win32Helper.GWL_EXSTYLE, new IntPtr(extendedStyle));

        // Strip all classic window chrome, including the thick resize frame. Pointer
        // hit targets in XAML provide resize behavior without the visible black ring.
        int style = Win32Helper.GetWindowLong(_hwnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER |
                   Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME);
        Win32Helper.SetWindowLong(_hwnd, Win32Helper.GWL_STYLE, style);
        Win32Helper.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE |
            Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        // Disable DWM border, rounding, and shadow so XAML/SystemBackdropElement owns
        // exact corner curvature with per-pixel alpha transparency.
        Win32Helper.DisableWindowFrameShadow(_hwnd);

        // Apply corner preference from settings (Square/Small/Round/Large).
        ApplyWindowCornerPreference();

    }

    private void OnAppearanceSettingsChanged()
    {
        void ApplyAppearance()
        {
            ApplyWindowCornerPreference();
            if (IsPopupVisible)
            {
                ApplyMaterialFromSettings();
                UpdatePanelVisibility();
                RefreshPreparedResultRows();
            }
            UpdateHotkeyHint();
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyAppearance();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ApplyAppearance);
        }
    }

    private void OnThemeServiceAppearanceChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyTheme();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ApplyTheme);
        }
    }

    private void ApplyTheme()
    {
        if (_themeService is null)
            return;

        var theme = _themeService.CurrentTheme;
        if (theme == ElementTheme.Default)
            theme = Win32Helper.IsSystemDarkMode() ? ElementTheme.Dark : ElementTheme.Light;

        RootGrid.RequestedTheme = theme;
        SyncEditRecommendationsButtonVisuals();
        if (RecommendedAppsGrid.IsEditMode)
        {
            RecommendedAppsGrid.RefreshEditVisuals();
        }

        ApplySearchInputWellVisuals();

        // A pre-warmed hidden shell intentionally owns no backdrop controller.
        // The current material is applied immediately before the next native show.
        if (IsPopupVisible)
        {
            ApplyMaterialFromSettings();
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        bool isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        _isPopupInputActive = isActive;
        if (_backdropConfiguration is not null)
        {
            _backdropConfiguration.IsInputActive = isActive;
        }

        if (isActive)
        {
            CancelUnfocusedAutoHideTimer();
            UpdateSelectionActions();
            if (IsPopupVisible &&
                !_isWindowClosed &&
                !IsAnyPopupFlyoutOpen &&
                !RecommendationOverlays.IsAnyOverlayOpen &&
                (_pendingInitialSearchBoxFocus ||
                 GetPopupFocusedElement() is var focused && (focused is null || ReferenceEquals(focused, RootGrid))))
            {
                _ = TryFocusSearchTextBoxForInput();
            }
        }
        else if (IsPopupVisible && !_isWindowClosed)
        {
            if (!IsPointerOverPopupWindow())
            {
                RecommendedAppsGrid.ClearHoveredCard();
                RecommendationOverlays.ClearHoveredCard();
                ClearAllResultRowTransientStates();
            }

            ArmUnfocusedAutoHideTimer();
            PopupDeactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Applies the XAML/SystemBackdropElement corner style from the widget appearance
    /// settings while keeping the outer HWND unrounded and shadowless.
    /// </summary>
    private void ApplyWindowCornerPreference()
    {
        string effectivePreference = WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
            SettingsService.WidgetCornerPreferenceLarge);
        int cornerPreference = Win32Helper.DWMWCP_DONOTROUND;

        Win32Helper.DisableWindowFrameShadow(_hwnd);
        Win32Helper.TrySetDwmWindowAttribute(
            _hwnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPreference);

        double radius = Math.Max(
            SearchPopupSurfacePolicy.OuterCornerRadius,
            WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(effectivePreference));
        var outerCornerRadius = new CornerRadius(radius);
        var bottomCornerRadius = new CornerRadius(0, 0, radius, radius);
        if (RootGrid.CornerRadius != default)
        {
            RootGrid.CornerRadius = default;
        }

        if (PopupMaterialSurface.CornerRadius != default)
        {
            PopupMaterialSurface.CornerRadius = default;
        }

        PopupSurfacePlate.CornerRadius = outerCornerRadius;
        PopupBorderOverlay.CornerRadius = outerCornerRadius;
        RecommendationOverlays.ApplyBottomCornerRadius(bottomCornerRadius);
        FooterAcrylicSurface.CornerRadius = bottomCornerRadius;
        _materialBackdropBridge?.SetCornerRadius(radius);
    }

    private void EnsureTransparentWindowBackdrop()
    {
        Win32Helper.EnableTransparentWindowHost(_hwnd);
        if (_transparentWindowBackdrop is null)
        {
            _transparentWindowBackdrop = new SolidTintSystemBackdrop(Colors.Transparent);
        }

        if (!ReferenceEquals(SystemBackdrop, _transparentWindowBackdrop))
        {
            SystemBackdrop = _transparentWindowBackdrop;
        }
    }

    private void ClearTransparentWindowBackdrop()
    {
        if (_transparentWindowBackdrop is not null)
        {
            if (ReferenceEquals(SystemBackdrop, _transparentWindowBackdrop))
            {
                SystemBackdrop = null;
            }

            _transparentWindowBackdrop.Dispose();
            _transparentWindowBackdrop = null;
        }

        Win32Helper.DisableTransparentWindowHost(_hwnd);
    }

    private ElementBackdropTargetBridge EnsurePopupMaterialBackdropBridge()
    {
        if (_materialBackdropBridge is null)
        {
            _materialBackdropBridge = new ElementBackdropTargetBridge(
                onTargetChanged: OnPopupMaterialBackdropTargetChanged,
                onTargetDisconnecting: OnPopupMaterialBackdropTargetDisconnecting);
        }

        _materialBackdropBridge.SetCornerRadius(PopupSurfacePlate.CornerRadius.TopLeft);
        if (!ReferenceEquals(PopupMaterialSurface.SystemBackdrop, _materialBackdropBridge))
        {
            PopupMaterialSurface.SystemBackdrop = _materialBackdropBridge;
        }

        _backdropTarget = _materialBackdropBridge.Target;
        return _materialBackdropBridge;
    }

    private void OnPopupMaterialBackdropTargetChanged()
    {
        if (_isWindowClosed)
        {
            return;
        }

        _backdropTarget = _materialBackdropBridge?.Target;
        if (_backdropTarget is not null && IsPopupVisible)
        {
            ApplyMaterialFromSettings();
        }
    }

    private void OnPopupMaterialBackdropTargetDisconnecting(ICompositionSupportsSystemBackdrop target)
    {
        if (ReferenceEquals(_backdropTarget, target))
        {
            DetachAcrylicControllerTarget();
            DetachMicaControllerTarget();
            _backdropTarget = null;
        }
    }

    private void ApplyMaterialFromSettings()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var accentColor = (App.Current as App)?.ThemeService?.GetEffectiveAccentColor()
                          ?? AccentColorHelper.DefaultAccentColor;

        var shell = _settingsService.Settings.WidgetShell;
        string materialType = WindowsCompatibilityService.ResolveWidgetMaterialType(
            shell.WidgetMaterialType);
        double surfaceOpacity = Math.Clamp(shell.WidgetOpacity, 0.0, 1.0);
        double materialIntensity = Math.Clamp(shell.WidgetMaterialIntensity, 0.0, 1.0);
        SearchPopupMaterialResolution materialResolution = SearchPopupSurfacePolicy.ResolveMaterial(
            materialType,
            surfaceOpacity,
            materialIntensity,
            isDark,
            accentColor);
        double effectiveCornerRadius = materialResolution.OuterCornerRadius;

        try
        {
            Win32Helper.SetWindowTheme(_hwnd, isDark);
            Win32Helper.DisableWindowFrameShadow(_hwnd);
            ApplyWindowCornerPreference();
            RootGrid.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;

            int backdropType;
            bool controllerApplied = false;
            bool awaitingElementTarget = false;

            if (materialType is SettingsService.WidgetMaterialTypeSolid || WindowsCompatibilityService.IsHighContrast)
            {
                EnsureTransparentWindowBackdrop();
                PopupMaterialSurface.Visibility = Visibility.Collapsed;
                DetachAcrylicControllerTarget();
                DetachMicaControllerTarget();
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.TrySetDwmWindowAttribute(_hwnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType);
                Win32Helper.DisableAccentPolicy(_hwnd);
                bool hc = WindowsCompatibilityService.IsHighContrast;
                var opaqueBase = hc
                    ? WindowsCompatibilityService.ResolveHighContrastBackgroundColor(isDark)
                    : BuildOpaquePopupSurfaceColor(isDark, accentColor, materialIntensity);
                PopupSurfacePlate.Background = new SolidColorBrush(opaqueBase);
            }
            else
            {
                EnsureTransparentWindowBackdrop();
                PopupMaterialSurface.Visibility = Visibility.Visible;
                ElementBackdropTargetBridge bridge = EnsurePopupMaterialBackdropBridge();
                if (bridge.Target is null)
                {
                    awaitingElementTarget = true;
                }
                else
                {
                    if (SettingsService.IsMicaMaterial(materialType))
                    {
                        controllerApplied = ApplyMicaController(
                            isDark,
                            BuildNativeBackdropTintColor(isDark, accentColor, materialIntensity),
                            materialType == SettingsService.WidgetMaterialTypeMicaAlt);
                    }

                    if (!controllerApplied && SettingsService.IsAcrylicMaterial(materialType))
                    {
                        controllerApplied = ApplyAcrylicController(
                            isDark,
                            BuildNativeBackdropTintColor(isDark, accentColor, materialIntensity),
                            materialResolution.EffectiveAcrylicSurfaceOpacity,
                            materialType == SettingsService.WidgetMaterialTypeAcrylicBase);
                    }
                }

                if (awaitingElementTarget)
                {
                    backdropType = Win32Helper.DWMSBT_NONE;
                    Win32Helper.TrySetDwmWindowAttribute(_hwnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType);
                    Win32Helper.DisableAccentPolicy(_hwnd);
                    PopupSurfacePlate.Background = new SolidColorBrush(materialResolution.SurfacePlateColor);
                }
                else if (controllerApplied)
                {
                    backdropType = Win32Helper.DWMSBT_NONE;
                    Win32Helper.TrySetDwmWindowAttribute(_hwnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType);
                    Win32Helper.DisableAccentPolicy(_hwnd);
                    PopupSurfacePlate.Background = new SolidColorBrush(materialResolution.SurfacePlateColor);
                }
                else if (effectiveCornerRadius <= 0)
                {
                    PopupMaterialSurface.Visibility = Visibility.Collapsed;
                    ClearTransparentWindowBackdrop();
                    backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                    Win32Helper.TrySetDwmWindowAttribute(_hwnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType);
                    DetachAcrylicControllerTarget();
                    DetachMicaControllerTarget();
                    double accentOpacity = WindowsCompatibilityService.UsesLegacyWindowAcrylic &&
                        SettingsService.IsAcrylicMaterial(materialType)
                            ? WidgetMaterialVisualCalculator.CalculateLegacyAcrylicOpacity(
                                materialType == SettingsService.WidgetMaterialTypeAcrylicBase,
                                surfaceOpacity,
                                materialIntensity)
                            : Math.Min(surfaceOpacity, 0.52);
                    Win32Helper.ApplyAccentBlur(
                        _hwnd,
                        BuildNativeBackdropTintColor(isDark, accentColor, materialIntensity),
                        accentOpacity,
                        true);
                    PopupSurfacePlate.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00));
                }
                else
                {
                    PopupMaterialSurface.Visibility = Visibility.Collapsed;
                    backdropType = Win32Helper.DWMSBT_NONE;
                    Win32Helper.TrySetDwmWindowAttribute(_hwnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType);
                    Win32Helper.DisableAccentPolicy(_hwnd);
                    PopupSurfacePlate.Background = new SolidColorBrush(
                        BuildOpaquePopupSurfaceColor(isDark, accentColor, materialIntensity));
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] ApplyMaterialFromSettings fallback: {ex.Message}");
            DisposeAcrylicController();
            DisposeMicaController();
            EnsureTransparentWindowBackdrop();
            PopupMaterialSurface.Visibility = Visibility.Collapsed;
            Win32Helper.DisableAccentPolicy(_hwnd);
            PopupSurfacePlate.Background = new SolidColorBrush(
                BuildOpaquePopupSurfaceColor(isDark, accentColor, materialIntensity));
        }

        var (thickness, borderColor) = WindowsCompatibilityService.IsHighContrast
            ? (1.0, WindowsCompatibilityService.ResolveHighContrastForegroundColor(isDark))
            : GetPopupBorderVisuals(isDark, accentColor);
        PopupBorderOverlay.BorderBrush = new SolidColorBrush(
            borderColor.A == 0 ? Colors.Transparent : borderColor);
        PopupBorderOverlay.BorderThickness = new Thickness(thickness);
        ApplySearchInputWellVisuals();
    }

    private void ApplySearchInputWellVisuals()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        bool isExpanded = BodyContentRow.Visibility == Visibility.Visible ||
                          TabsAndFilterRow.Visibility == Visibility.Visible ||
                          EverythingPromptBanner.Visibility == Visibility.Visible ||
                          FooterBar.Visibility == Visibility.Visible;
        SearchPopupInputWellVisuals visuals = SearchPopupSurfacePolicy.ResolveInputWellVisuals(
            isExpanded,
            isDark);
        SearchBoxBorder.Background = visuals.BackgroundColor.A == 0
            ? SearchRecommendationLayoutEngine.TransparentCardBrush
            : new SolidColorBrush(visuals.BackgroundColor);
        SearchBoxBorder.BorderBrush = visuals.BorderColor.A == 0
            ? SearchRecommendationLayoutEngine.TransparentCardBrush
            : new SolidColorBrush(visuals.BorderColor);
        HeaderBottomDivider.Visibility = visuals.ShowHeaderBottomDivider
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Builds the tint color for native backdrop materials by blending the base
    /// surface color with the accent color according to material intensity.
    /// </summary>
    private static Windows.UI.Color BuildNativeBackdropTintColor(
        bool isDark,
        Windows.UI.Color accentColor,
        double materialIntensity)
    {
        var baseColor = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        double accentMix = 0.07 * materialIntensity;
        return BlendColors(baseColor, accentColor, accentMix);
    }

    private bool ApplyMicaController(bool isDark, Windows.UI.Color tintColor, bool useAlt)
    {
        if (!WindowsCompatibilityService.SupportsMica)
        {
            DisposeMicaController();
            return false;
        }

        _backdropTarget = EnsurePopupMaterialBackdropBridge().Target;
        if (_backdropTarget is null)
        {
            return false;
        }
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        DetachAcrylicControllerTarget();
        if (_micaController is null)
        {
            _micaController = new MicaController();
            _micaControllerAttached = false;
        }

        _micaController.Kind = useAlt ? MicaKind.BaseAlt : MicaKind.Base;

        if (!_micaControllerAttached)
        {
            if (!_micaController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeMicaController();
                return false;
            }

            _micaControllerAttached = true;
        }

        _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);

        _micaController.TintColor = tintColor;
        _micaController.FallbackColor = useAlt
            ? isDark
                ? Windows.UI.Color.FromArgb(0xFF, 0x16, 0x18, 0x1D)
                : Windows.UI.Color.FromArgb(0xFF, 0xE8, 0xEA, 0xEF)
            : isDark
                ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x22, 0x26)
                : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        double intensity = Math.Clamp(_settingsService.Settings.WidgetMaterialIntensity, 0.0, 1.0);
        double tintOpacity = useAlt
            ? Lerp(0.28, 0.82, intensity)
            : Lerp(0.04, 0.46, intensity);
        double luminosityOpacity = useAlt
            ? Lerp(isDark ? 0.34 : 0.42, isDark ? 0.72 : 0.76, intensity)
            : Lerp(isDark ? 0.78 : 0.82, isDark ? 0.94 : 0.96, intensity);

        _micaController.TintOpacity = (float)tintOpacity;
        _micaController.LuminosityOpacity = (float)luminosityOpacity;
        return true;
    }

    private bool ApplyAcrylicController(bool isDark, Windows.UI.Color tintColor, double surfaceOpacity, bool useBase)
    {
        if (!WindowsCompatibilityService.SupportsDesktopAcrylic)
        {
            DisposeAcrylicController();
            return false;
        }

        _backdropTarget = EnsurePopupMaterialBackdropBridge().Target;
        if (_backdropTarget is null)
        {
            return false;
        }
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        _backdropConfiguration.HighContrastBackgroundColor = WindowsCompatibilityService.ResolveHighContrastBackgroundColor(isDark);

        DetachMicaControllerTarget();
        if (_acrylicController is null || _acrylicController.IsClosed)
        {
            DisposeAcrylicController();
            _acrylicController = new DesktopAcrylicController();
            _acrylicControllerAttached = false;
        }

        _acrylicController.Kind = useBase ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Thin;

        if (!_acrylicControllerAttached)
        {
            if (!_acrylicController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }

            _acrylicControllerAttached = true;
        }

        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);

        _acrylicController.TintColor = tintColor;
        _acrylicController.FallbackColor = tintColor;

        double intensity = Math.Clamp(_settingsService.Settings.WidgetMaterialIntensity, 0.0, 1.0);
        double surfaceStrength = Lerp(0.08, 1.0, Math.Clamp(surfaceOpacity, 0.0, 1.0));
        double tintOpacity = useBase
            ? Lerp(isDark ? 0.18 : 0.12, isDark ? 0.72 : 0.62, intensity)
            : Lerp(isDark ? 0.04 : 0.02, isDark ? 0.42 : 0.34, intensity);
        double luminosityOpacity = useBase
            ? Lerp(isDark ? 0.38 : 0.46, isDark ? 0.82 : 0.90, intensity)
            : Lerp(isDark ? 0.16 : 0.22, isDark ? 0.56 : 0.64, intensity);

        _acrylicController.TintOpacity = (float)Math.Clamp(tintOpacity * surfaceStrength, 0.0, 1.0);
        _acrylicController.LuminosityOpacity = (float)Math.Clamp(luminosityOpacity * surfaceStrength, 0.0, 1.0);
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
            _micaControllerAttached = false;
        }
    }

    private void DetachMicaControllerTarget()
    {
        if (_micaController is null || !_micaControllerAttached)
        {
            return;
        }

        try
        {
            _micaController.RemoveAllSystemBackdropTargets();
        }
        catch
        {
        }
        finally
        {
            _micaControllerAttached = false;
        }
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
            _acrylicControllerAttached = false;
        }
    }

    private void DetachAcrylicControllerTarget()
    {
        if (_acrylicController is null || !_acrylicControllerAttached)
        {
            return;
        }

        try
        {
            _acrylicController.RemoveAllSystemBackdropTargets();
        }
        catch
        {
        }
        finally
        {
            _acrylicControllerAttached = false;
        }
    }

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * Math.Clamp(progress, 0.0, 1.0));

    /// <summary>
    /// Mirrors the widget border visuals (style thickness + neutral/accent color mode)
    /// so the popup edge matches the desktop widgets.
    /// </summary>
    private (double Thickness, Windows.UI.Color BorderColor) GetPopupBorderVisuals(
        bool isDark, Windows.UI.Color accentColor)
    {
        string borderStyle = _settingsService.Settings.WidgetBorderStyle;
        string colorMode = _settingsService.Settings.WidgetBorderColorMode;
        var (thickness, alpha) = borderStyle switch
        {
            SettingsService.WidgetBorderStyleMedium => (1.2d, (byte)0x30),
            SettingsService.WidgetBorderStyleThick => (1.6d, (byte)0x48),
            SettingsService.WidgetBorderStyleNone => (0d, (byte)0),
            _ => (0.8d, (byte)0x18)
        };

        if (thickness <= 0 || colorMode == SettingsService.WidgetBorderColorModeNone)
        {
            byte fallbackChannel = isDark ? (byte)0xFF : (byte)0x00;
            byte fallbackAlpha = isDark ? (byte)0x20 : (byte)0x16;
            return (0.8d, Windows.UI.Color.FromArgb(fallbackAlpha, fallbackChannel, fallbackChannel, fallbackChannel));
        }

        bool useAccent = colorMode == SettingsService.WidgetBorderColorModeAccent;
        byte borderAlpha = useAccent
            ? (byte)Math.Clamp(Math.Round(alpha * 1.35), 0, 255)
            : alpha;
        byte red = useAccent ? accentColor.R : isDark ? (byte)0xFF : (byte)0x00;
        byte green = useAccent ? accentColor.G : isDark ? (byte)0xFF : (byte)0x00;
        byte blue = useAccent ? accentColor.B : isDark ? (byte)0xFF : (byte)0x00;
        return (thickness, Windows.UI.Color.FromArgb(borderAlpha, red, green, blue));
    }

    // Solid-mode surface color. Unlike the widget frosted surface, the popup
    // blend never applies surface opacity: transparency is not supported for
    // the popup's solid material.
    internal static Windows.UI.Color BuildOpaquePopupSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        double materialIntensity)
    {
        var baseColor = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x21, 0x24, 0x2A)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        // Blend accent color into the base according to intensity.
        double accentMix = 0.05 * materialIntensity;
        return BlendColors(baseColor, accentColor, accentMix);
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

    private void SetupBindings()
    {
        UpdateHotkeyHint();
        SearchTextBox.PlaceholderText = _localizationService.T("Search.Placeholder");
        NoResultsTitle.Text = _localizationService.T("Search.NoResults.Title");
        NoResultsSubtitle.Text = _localizationService.T("Search.NoResults.Subtitle");
        NoResultsEverythingHint.Text = _localizationService.T("Search.NoResults.EverythingHint");
        EmptyTabHintText.Text = _localizationService.T("Search.Tab.Empty");

        OpenSettingsLabel.Text = _localizationService.T("Search.Action.OpenSettings");

        ToolTipService.SetToolTip(ClearQueryButton, _localizationService.T("Common.Close"));
        SortNameLabel.Text = _localizationService.T("Search.Sort.Name");
        SortTypeLabel.Text = _localizationService.T("Search.Sort.Type");
        SortSizeLabel.Text = _localizationService.T("Search.Sort.Size");
        SortDateLabel.Text = _localizationService.T("Search.Sort.Date");

        ToolTipService.SetToolTip(
            HiddenAppsEntryButton,
            _localizationService.T("Search.Recommend.HiddenTitle"));
        ToolTipService.SetToolTip(
            EverythingDismissButton,
            _localizationService.T("Search.EverythingBanner.DontRemind"));
        RecommendedAppsGrid.ApplyLocalizedStrings();
        RecommendationOverlays.ApplyLocalizedStrings();
        SyncEditRecommendationsButtonVisuals();
        ApplyLocalizedCommandPaletteStrings();
        ApplyLocalizedCategoryTabsStrings();

        TabsList.ItemsSource = _viewModel.Tabs;
        ResultsRepeater.ItemsSource = _viewModel.CurrentResults;
        RecommendedAppsGrid.BindItemsSource();
        RecommendationOverlays.BindItemsSources();

        UpdateEverythingPromptBanner();
        UpdateRecommendationHeaderState();
        UpdatePanelVisibility();
        UpdateSortHeaders();
    }

    private void UpdateHotkeyHint()
    {
        string hint = _viewModel.HotkeyHint;
        HotkeyHintText.Text = hint;
        bool hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
        ClearQueryButton.Visibility = hasText
            ? Visibility.Visible
            : Visibility.Collapsed;
        HotkeyHintBadge.Visibility = !hasText &&
                                     _latchedInlineScope == SearchInlineScope.None &&
                                     !string.IsNullOrWhiteSpace(hint)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool HasOpenModalOrContextPopup()
    {
        if (IsAnyPopupFlyoutOpen)
        {
            return true;
        }

        if (Content?.XamlRoot is { } xamlRoot)
        {
            var openPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
            for (int i = 0; i < openPopups.Count; i++)
            {
                if (openPopups[i].Child is ContentDialog or MenuFlyoutPresenter or FlyoutPresenter)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || HasOpenModalOrContextPopup() ||
            DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (!RecommendationOverlays.IsAnyOverlayOpen &&
            !RecommendationOverlays.IsEditingFolderTitle &&
            HandleCommandPaletteShortcutKeys(e))
        {
            e.Handled = true;
            return;
        }

        // Intercept tab cycling before ListView/ScrollViewer keyboard handling.
        // Otherwise a tab click can leave focus in their internal presenters and
        // the next arrow key scrolls the view without changing SelectedItem.
        if (e.Key == Windows.System.VirtualKey.Tab &&
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            bool backward = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
            _viewModel.CycleTab(backward);
            e.Handled = true;
            return;
        }

        if (!RecommendationOverlays.IsAnyOverlayOpen &&
            !RecommendationOverlays.IsEditingFolderTitle &&
            RecommendedAppsGrid.IsAppsPanelVisible &&
            string.IsNullOrEmpty(SearchTextBox.Text))
        {
            bool plainArrowKey =
                e.Key is Windows.System.VirtualKey.Up
                    or Windows.System.VirtualKey.Down
                    or Windows.System.VirtualKey.Left
                    or Windows.System.VirtualKey.Right &&
                !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) &&
                !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu) &&
                !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
            bool routeToRecommend =
                plainArrowKey ||
                (RecommendedAppsGrid.IsEditMode &&
                 ((e.Key == Windows.System.VirtualKey.Delete && RecommendedAppsGrid.MultiSelectedCount > 0) ||
                  (e.Key == Windows.System.VirtualKey.A &&
                   Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))));
            if (routeToRecommend && RecommendedAppsGrid.HandleKey(e))
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down))
        {
            return;
        }

        if (RecommendationOverlays.IsAnyOverlayOpen)
        {
            return;
        }

        if (RecommendedAppsGrid.IsAppsPanelVisible)
        {
            if (RecommendedAppsGrid.HandleKey(e))
            {
                e.Handled = true;
            }
            return;
        }

        if (TryMoveResultSelection(e.Key))
        {
            e.Handled = true;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        // ── Recommended apps panel takes priority when visible ──
        bool recommendedVisible =
            RecommendedAppsGrid.IsAppsPanelVisible &&
            !RecommendationOverlays.IsAnyOverlayOpen;

        // Escape: close active folder/hidden overlays first, then clear recommended
        // app selection, then hand focus back from a sort header to the search box,
        // then hide popup.
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (_isManageTabsFlyoutOpen)
            {
                ManageTabsFlyout.Hide();
                e.Handled = true;
                return;
            }
            if (_isCmdBarFlyoutOpen)
            {
                CmdBarActionsFlyout.Hide();
                e.Handled = true;
                return;
            }
            if (HasOpenModalOrContextPopup())
            {
                return;
            }
            if (RecommendationOverlays.IsEditingFolderTitle)
            {
                RecommendationOverlays.CancelTitleEdit();
                e.Handled = true;
                return;
            }
            if (RecommendationOverlays.IsAnyOverlayOpen)
            {
                RecommendationOverlays.CloseOverlays();
                SearchTextBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }
            if (RecommendedAppsGrid.IsEditMode)
            {
                if (RecommendedAppsGrid.MultiSelectedCount > 0)
                {
                    RecommendedAppsGrid.ClearEditMultiSelection();
                }
                else
                {
                    RecommendedAppsGrid.DisableEditMode();
                }

                SearchTextBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }
            if (_multiSelectedItems.Count > 0)
            {
                ClearMultiSelection();
                SearchTextBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }
            if (RecommendedAppsGrid.SelectedIndex >= 0)
            {
                RecommendedAppsGrid.ClearSelection();
                SearchTextBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }
            if (GetPopupFocusedElement() is Button focusedHeader && IsSortHeaderButton(focusedHeader))
            {
                SearchTextBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }
            if (_latchedInlineScope != SearchInlineScope.None)
            {
                ExitLatchedScope();
                e.Handled = true;
                return;
            }
            HidePopup();
            e.Handled = true;
            return;
        }

        // Ctrl+Tab / Ctrl+Shift+Tab — cycle through search tabs
        if (e.Key == Windows.System.VirtualKey.Tab &&
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            bool backward = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
            _viewModel.CycleTab(backward);
            e.Handled = true;
            return;
        }

        // Recommended apps keyboard navigation (arrows, Enter, Space).
        if (recommendedVisible && RecommendedAppsGrid.HandleKey(e))
        {
            e.Handled = true;
            return;
        }

        // PreviewKeyDown normally owns result navigation. Keep this bubbling
        // fallback for controls that do not participate in the preview route.
        if (e.Key == Windows.System.VirtualKey.Up || e.Key == Windows.System.VirtualKey.Down)
        {
            if (TryMoveResultSelection(e.Key))
            {
                e.Handled = true;
            }
            return;
        }

        // Space — QuickLook preview.
        if (e.Key == Windows.System.VirtualKey.Space)
        {
            // If a recommended app is selected, preview it even when focus
            // is in the search text box.
            if (RecommendedAppsGrid.SelectedIndex >= 0)
            {
                TryPreviewSelectedItem();
                e.Handled = true;
                return;
            }
            // Standard case: preview only when focus is outside editable controls.
            if (!IsTextInputCurrentlyFocused())
            {
                TryPreviewSelectedItem();
                e.Handled = true;
            }
        }
    }

    private void RootGrid_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (e.Handled ||
            IsAnyPopupFlyoutOpen ||
            RecommendationOverlays.IsEditingFolderTitle ||
            RecommendationOverlays.IsAnyOverlayOpen ||
            DeskBox.Helpers.ImeCompositionGuard.IsComposingOrWithinGrace() ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.LeftWindows) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.RightWindows))
        {
            return;
        }

        if (IsTextInputCurrentlyFocused())
        {
            return;
        }

        object? focused = GetPopupFocusedElement();
        if (focused is ComboBox ||
            (focused is Button focusedBtn &&
             !IsSortHeaderButton(focusedBtn) &&
             !RecommendedAppsGrid.IsAppsPanelVisible))
        {
            return;
        }

        char ch = e.Character;
        if (ch == 0 || char.IsControl(ch) || ch == ' ')
        {
            return;
        }

        if (TryRedirectCharacterToSearchBox(ch))
        {
            e.Handled = true;
        }
    }

    private object? GetPopupFocusedElement() =>
        RootGrid.XamlRoot is { } xamlRoot
            ? FocusManager.GetFocusedElement(xamlRoot)
            : null;

    private bool IsTextInputCurrentlyFocused()
    {
        if (SearchTextBox.FocusState != FocusState.Unfocused)
        {
            return true;
        }

        object? focused = GetPopupFocusedElement();
        return focused is TextBox or RichEditBox or PasswordBox or AutoSuggestBox or NumberBox ||
               (focused is DependencyObject dep &&
                DeskBox.Helpers.TransientInputFocusGuard.IsInsideTextInputControl(dep));
    }

    /// <summary>
    /// The four result sort headers are the only buttons on the tab chain
    /// whose character input must flow into the query instead of being
    /// dropped; the filter combos and other controls keep their own editing.
    /// </summary>
    private bool IsSortHeaderButton(Button button) =>
        button == SortNameHeader || button == SortTypeHeader ||
        button == SortSizeHeader || button == SortDateHeader;

    private bool TryMoveResultSelection(Windows.System.VirtualKey key)
    {
        if (key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down) ||
            !_viewModel.IsQueryActive ||
            !_viewModel.HasCurrentResults)
        {
            return false;
        }

        object? focusedElement = GetPopupFocusedElement();
        DependencyObject? focusedObject = focusedElement as DependencyObject;
        TextBox? focusedTextBox = FindVisualAncestor<TextBox>(focusedObject);
        bool searchInputFocused = SearchTextBox.FocusState != FocusState.Unfocused ||
                                  ReferenceEquals(focusedTextBox, SearchTextBox);
        if (focusedTextBox is not null && !searchInputFocused)
        {
            // Do not take arrow keys from rename/dialog text editors.
            return false;
        }

        SearchResultItem? previousSelection = _viewModel.SelectedItem;
        if (key == Windows.System.VirtualKey.Up)
        {
            _viewModel.MoveSelectionUp();
        }
        else
        {
            _viewModel.MoveSelectionDown();
        }

        _isNavigatingResults = true;

        // Moving at a boundary or switching to a tab whose first item has the
        // same identity does not raise SelectedItem again. Explicitly refresh in
        // that case so a recycled row cannot remain visually unselected.
        if (ReferenceEquals(previousSelection, _viewModel.SelectedItem))
        {
            UpdateSelectionHighlight();
        }

        if (!searchInputFocused)
        {
            FocusSelectedResult();
        }

        return true;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element)
        where T : DependencyObject =>
        SearchRecommendationLayoutEngine.FindVisualAncestor<T>(element);

    /// <summary>
    /// Handles newly realized item containers in the recommended-apps repeater.
    /// Refreshes the icon image source because SearchResultItem.Icon is populated
    /// lazily by FileMetaService and is not observable, so XAML binding alone
    /// cannot track it.
    /// </summary>

    private void OnRecommendationIconsResolved()
    {
        if (IsPopupVisible)
        {
            RecommendedAppsGrid.RefreshIcons();
        }
    }

    private void OnRecommendationLayoutUpdated()
    {
        if (!IsPopupVisible)
        {
            return;
        }

        RecommendedAppsGrid.RefreshAfterLayoutUpdate();
        UpdateRecommendationHeaderState();
        RecommendationOverlays.SyncWithViewModel();
        UpdateRecommendationEditStatusHint();
        RecommendedAppsGrid.RefreshIcons();

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_isWindowClosed || !IsPopupVisible)
            {
                return;
            }

            RecommendedAppsGrid.QueueDeferredEditRefresh();
        });
    }

    private void UpdateEverythingPromptBanner()
    {
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchTextBox.Text);
        bool allowBanner = hasQuery || _settingsService.Settings.Search.SearchShowRecommendations;
        EverythingPromptBanner.Visibility = allowBanner && _viewModel.IsEverythingPromptVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        EverythingPromptTextBlock.Text = _viewModel.EverythingPromptText;
        EverythingConnectButtonText.Text = _viewModel.EverythingPromptActionText;
        EverythingPromptIcon.Glyph = _viewModel.IsEverythingPromptWarning
            ? "\uE7BA"
            : "\uE946";
    }

    private void EverythingConnectButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.InvokeAction("open-settings");
    }

    private void EverythingDismissButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DismissEverythingPrompt();
        UpdatePanelVisibility();
    }

    private void UpdateRecommendationHeaderState()
    {
        bool homeVisible = string.IsNullOrWhiteSpace(SearchTextBox.Text) &&
                           RecommendedAppsGrid.IsAppsPanelVisible;
        bool hasHidden = _viewModel.HasHiddenRecommendations;
        bool hasEntries = _viewModel.HasRecommendedEntries;

        HiddenAppsEntryButton.Visibility = homeVisible && hasHidden
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (hasHidden)
        {
            HiddenAppsEntryLabel.Text = string.Format(
                _localizationService.T("Search.Recommend.HiddenEntryFormat"),
                _viewModel.HiddenRecommendationCount);
        }

        EditRecommendationsButton.Visibility = homeVisible && hasEntries
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!hasEntries && RecommendedAppsGrid.IsEditMode)
        {
            RecommendedAppsGrid.DisableEditMode();
        }

        RecommendedAppsGrid.SetHomeContentState(hasEntries, hasHidden);

        if (!hasHidden && RecommendationOverlays.IsHiddenOverlayVisible)
        {
            RecommendationOverlays.CloseOverlays();
        }
    }

    private void UpdateScopePrefixBadge(string? rawQuery)
    {
        SyncScopeHeaderVisuals(rawQuery);
    }

    private void UpdatePanelVisibility() => UpdatePanelVisibility(forceBoundsWhenOpening: false);

    private void UpdatePanelVisibility(bool forceBoundsWhenOpening)
    {
        bool wasResultsVisible = ResultsPanel.Visibility == Visibility.Visible;
        bool hasQuery = HasActiveSearchOrScope;
        if (!hasQuery)
        {
            ResetSearchSessionVisualFlags();
        }
        bool searching = _viewModel.IsSearching;
        bool hasResults = _viewModel.HasResults;
        bool tabHasItems = _viewModel.HasCurrentResults;
        bool tabSelected = _viewModel.SelectedTab is not null;
        bool recommendationsEnabled = _settingsService.Settings.Search.SearchShowRecommendations;

        UpdateEverythingPromptBanner();
        UpdateScopePrefixBadge(SearchTextBox.Text);
        UpdateHotkeyHint();

        // A ready resident index answers normal keystrokes directly. Avoid flashing a
        // progress animation for the short cancellable query window; indexing state
        // remains available in Settings for first-build and rebuild diagnostics.
        SearchProgressBar.IsIndeterminate = false;
        SearchProgressBar.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingStatusText.Text = _localizationService.T("Search.Status.Searching");
        LoadingPanel.Visibility = Visibility.Collapsed;

        bool showResults = hasQuery && hasResults && tabHasItems;
        bool showNoResults = hasQuery && !searching && !hasResults;
        bool showResultChrome = hasQuery && hasResults && tabSelected;

        bool showCategoryBar = showResultChrome && _viewModel.ShouldShowCategoryBar;
        TabsList.Visibility = showCategoryBar ? Visibility.Visible : Visibility.Collapsed;
        TabsAndFilterRow.Visibility = showCategoryBar ? Visibility.Visible : Visibility.Collapsed;

        NoResultsPanel.Visibility = showNoResults
            ? Visibility.Visible
            : Visibility.Collapsed;

        // File-name search is fully delegated to Everything and requires the
        // user's opt-in. When nothing matched and that provider is not
        // connected, say so instead of leaving an unexplained empty state
        // (feedback 291).
        NoResultsEverythingHint.Visibility =
            NoResultsPanel.Visibility == Visibility.Visible &&
            _viewModel.IsFileSearchProviderUnavailable
                ? Visibility.Visible
                : Visibility.Collapsed;

        ResultsPanel.Visibility = showResults ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetailsSplitLayout(showResults);
        UpdateCommandBarForSelectedItem();

        bool holdRecommendationsForInitialSearch =
            hasQuery &&
            searching &&
            !hasResults &&
            !showNoResults &&
            !_hasExpandedResultsInQuerySession &&
            recommendationsEnabled &&
            RecommendedAppsGrid.IsAppsPanelVisible;
        bool showRecommendedApps = (!hasQuery || holdRecommendationsForInitialSearch) &&
            recommendationsEnabled &&
            (tabHasItems ||
             _viewModel.HasRecommendedEntries ||
             _viewModel.HasHiddenRecommendations ||
             RecommendedAppsGrid.IsSkeletonVisible);
        if (showRecommendedApps)
        {
            bool wasHidden = !RecommendedAppsGrid.IsAppsPanelVisible;
            RecommendedAppsGrid.SetAppsPanelVisibility(true);
            UpdateRecommendationHeaderState();
            RecommendedAppsGrid.RefreshIcons();

            if (wasHidden &&
                !_suppressPanelEntranceAnimation &&
                !_hasPlayedOpenRecommendationsEntrance)
            {
                _hasPlayedOpenRecommendationsEntrance = true;
                RecommendedAppsGrid.AnimateEntrance();
            }
        }
        else
        {
            RecommendedAppsGrid.SetAppsPanelVisibility(false);
            RecommendedAppsGrid.DisableEditMode();
            UpdateRecommendationHeaderState();
            RecommendedAppsGrid.CancelDrag();
            RecommendationOverlays.CloseOverlays(immediate: true);
            RecommendedAppsGrid.ClearSelection();
            RecommendedAppsGrid.ClearHoveredCard();
        }

        UpdateRecommendationEditStatusHint();

        // Optional sortable header: hidden by default unless enabled in Search Settings or Ctrl+K.
        bool fileSortTab = _viewModel.SelectedTab?.SupportsFileSort == true;
        bool showSortHeader = showResults &&
                              fileSortTab &&
                              _settingsService.Settings.Search.SearchShowSortHeader;
        if (!showSortHeader &&
            IsPopupVisible &&
            !_isWindowClosed &&
            GetPopupFocusedElement() is Button focusedSortBtn &&
            IsSortHeaderButton(focusedSortBtn))
        {
            SearchTextBox.Focus(FocusState.Programmatic);
        }

        SortHeaderRow.Visibility = showSortHeader
            ? Visibility.Visible
            : Visibility.Collapsed;
        SyncSortHeaderAndRowColumns();
        if (showSortHeader)
        {
            UpdateSortHeaders();
        }

        bool showEmptySelectedTab = hasQuery && !searching && hasResults && !tabHasItems && tabSelected;
        if (showEmptySelectedTab)
        {
            EmptyTabHintIcon.Glyph = _viewModel.SelectedTab?.Glyph ?? "\uE8B7";
        }
        EmptyTabHintPanel.Visibility = showEmptySelectedTab
            ? Visibility.Visible
            : Visibility.Collapsed;

        BodyContentRow.Visibility =
            (showResults || showNoResults || showRecommendedApps || showEmptySelectedTab ||
             RecommendedAppsGrid.IsSkeletonVisible)
                ? Visibility.Visible
                : Visibility.Collapsed;

        UpdateSelectionActions();
        ApplySearchInputWellVisuals();

        if (!wasResultsVisible &&
            !_hasExpandedResultsInQuerySession &&
            showResults &&
            !forceBoundsWhenOpening &&
            !_viewModel.IsApplyingBackgroundResultRefresh)
        {
            _hasExpandedResultsInQuerySession = true;
            AnimateResultsFirstExpand();
        }
        else if (showResults)
        {
            _hasExpandedResultsInQuerySession = true;
        }
        else if (!showResults)
        {
            ResultsPanel.Opacity = 1;
            ResultsTranslateTransform.Y = 0;
        }

        UpdatePopupDynamicBounds(forceBoundsWhenOpening);
    }

    private void UpdatePopupDynamicBounds(bool forceWhenOpening = false)
    {
        if (_appWindow is null || _isWindowClosed || _isWindowDragging || _isWindowResizing)
        {
            return;
        }

        if (!IsPopupVisible && !forceWhenOpening)
        {
            return;
        }

        var searchSlice = _settingsService.Settings.Search;
        int? customX = searchSlice.SearchPopupCustomX;
        int? customY = searchSlice.SearchPopupCustomY;

        double scale = customX is int targetX && customY is int targetY
            ? Win32Helper.GetDpiScaleForPoint(targetX, targetY)
            : Win32Helper.GetDpiScaleForWindow(_hwnd, Content?.XamlRoot);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1.0;
        }

        DisplayArea displayArea = customX is int savedX && customY is int savedY
            ? DisplayArea.GetFromPoint(new PointInt32(savedX, savedY), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workAreaPhysical = displayArea.WorkArea;
        int workAreaLogicalWidth = Math.Max(320, (int)Math.Round(workAreaPhysical.Width / scale));
        int workAreaLogicalHeight = Math.Max(320, (int)Math.Round(workAreaPhysical.Height / scale));
        int desiredLogicalWidth = SearchPopupSurfacePolicy.ResolveEffectiveLogicalWidth(
            searchSlice.SearchPopupCustomWidth,
            workAreaLogicalWidth);

        bool hasQuery = HasActiveSearchOrScope;
        bool showDetailsPane = DetailsPaneCard.Visibility == Visibility.Visible;
        bool showNoResultsOrEmptyTab = NoResultsPanel.Visibility == Visibility.Visible ||
                                       EmptyTabHintPanel.Visibility == Visibility.Visible;

        // When the very first keystroke triggers an async query, hold the current window
        // geometry steady during the few milliseconds before ApplySearchResponse arrives
        // instead of collapsing downward toward 56px and immediately bouncing back out.
        if (!forceWhenOpening &&
            hasQuery &&
            _viewModel.IsSearching &&
            !_viewModel.HasResults &&
            !showNoResultsOrEmptyTab)
        {
            return;
        }

        bool showRecommendations = RecommendedAppsGrid.IsAppsPanelVisible ||
                                   RecommendedAppsGrid.IsSkeletonVisible;
        int recCount = RecommendedAppsGrid.IsSkeletonVisible
            ? Math.Max(6, _viewModel.RecommendedEntries.Count)
            : _viewModel.RecommendedEntries.Count;

        var layoutState = new SearchPopupLayoutState(
            HasQuery: hasQuery,
            IsSearching: _viewModel.IsSearching,
            ResultCount: ResultsPanel.Visibility == Visibility.Visible ? _viewModel.CurrentResults.Count : 0,
            ShowFilterBar: TabsAndFilterRow.Visibility == Visibility.Visible,
            ShowSortHeader: SortHeaderRow.Visibility == Visibility.Visible,
            ShowEverythingBanner: EverythingPromptBanner.Visibility == Visibility.Visible,
            ShowRecommendations: showRecommendations,
            RecommendationEntryCount: recCount,
            ShowAllHiddenRecommendationsHint: RecommendedAppsGrid.IsAllHiddenHintVisible,
            ShowNoResults: showNoResultsOrEmptyTab,
            ShowFooter: FooterBar.Visibility == Visibility.Visible,
            ShowDetailsPane: showDetailsPane,
            PopupWidthLogical: desiredLogicalWidth);

        int maxLogicalHeight = SearchPopupSurfacePolicy.ComputeMaxLogicalHeight(workAreaLogicalHeight);
        int desiredLogicalHeight = SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(
            layoutState,
            workAreaLogicalHeight);

        // Suppress scrollbar unless content genuinely hits the max-height ceiling or pages more results.
        ResultsPanel.VerticalScrollBarVisibility =
            (ResultsPanel.Visibility == Visibility.Visible &&
             (desiredLogicalHeight >= maxLogicalHeight || _viewModel.HasMoreResults))
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Hidden;

        int? minOverlayHeightPhysical = RecommendationOverlays.IsAnyOverlayOpen
            ? SearchPopupSurfacePolicy.ToPhysicalPixels(420, scale)
            : null;

        RectInt32 targetBounds = SearchPopupSurfacePolicy.ResolveAnchoredWindowBounds(
            workAreaPhysical,
            scale,
            desiredLogicalHeight,
            customX,
            customY,
            minOverlayHeightPhysical,
            desiredLogicalWidth);

        double targetLogicalWidth = Math.Max(
            SearchPopupSurfacePolicy.MinPopupWidthLogical,
            Math.Round(targetBounds.Width / scale, 2));
        double targetLogicalHeight = Math.Max(
            SearchPopupSurfacePolicy.CapsuleOnlyHeightLogical,
            Math.Round(targetBounds.Height / scale, 2));

        bool animateMorph = !forceWhenOpening &&
                            IsPopupVisible &&
                            AreSystemAnimationsEnabled() &&
                            !RecommendationOverlays.IsAnyOverlayOpen &&
                            double.IsFinite(RootGrid.Height) &&
                            RootGrid.Height > 0 &&
                            double.IsFinite(RootGrid.Width) &&
                            RootGrid.Width > 0;

        if (!animateMorph)
        {
            SnapMorphBoundsToTarget(targetLogicalWidth, targetLogicalHeight, targetBounds);
            return;
        }

        bool isSpringRunning = IsMorphSpringRunning;
        if (!isSpringRunning || !double.IsFinite(_heightSpringCurrentLogical) || _heightSpringCurrentLogical <= 0)
        {
            _heightSpringCurrentLogical = RootGrid.Height;
            _heightSpringVelocity = 0;
        }

        if (!isSpringRunning || !double.IsFinite(_widthSpringCurrentLogical) || _widthSpringCurrentLogical <= 0)
        {
            _widthSpringCurrentLogical = RootGrid.Width;
            _widthSpringVelocity = 0;
        }

        bool widthAnimating = Math.Abs(targetLogicalWidth - _widthSpringCurrentLogical) >= 0.5 ||
                              Math.Abs(_widthSpringVelocity) >= 3.0;
        bool heightAnimating = Math.Abs(targetLogicalHeight - _heightSpringCurrentLogical) >= 0.5 ||
                               Math.Abs(_heightSpringVelocity) >= 3.0;

        if (!widthAnimating && !heightAnimating)
        {
            SnapMorphBoundsToTarget(targetLogicalWidth, targetLogicalHeight, targetBounds);
            return;
        }

        PointInt32 livePos = _appWindow.Position;
        SizeInt32 liveSize = _appWindow.Size;
        int headroomPhysical = SearchPopupSurfacePolicy.ToPhysicalPixels(
            SearchPopupSurfacePolicy.SpringHeadroomLogical,
            scale);
        int maxDownwardPhysical = Math.Max(
            targetBounds.Height,
            (workAreaPhysical.Y + workAreaPhysical.Height - 8) - targetBounds.Y);
        int immediatePhysicalHeight = Math.Min(
            maxDownwardPhysical,
            Math.Max(liveSize.Height, targetBounds.Height + headroomPhysical));
        int immediatePhysicalWidth = widthAnimating
            ? Math.Max(liveSize.Width, targetBounds.Width + headroomPhysical)
            : Math.Max(liveSize.Width, targetBounds.Width);
        int startX = widthAnimating ? livePos.X : targetBounds.X;

        if (RootGrid.HorizontalAlignment != HorizontalAlignment.Left)
        {
            RootGrid.HorizontalAlignment = HorizontalAlignment.Left;
        }

        if (livePos.X != startX ||
            livePos.Y != targetBounds.Y ||
            liveSize.Width < immediatePhysicalWidth ||
            liveSize.Height < immediatePhysicalHeight)
        {
            _appWindow.MoveAndResize(new RectInt32(
                startX,
                targetBounds.Y,
                immediatePhysicalWidth,
                immediatePhysicalHeight));
        }

        _morphWorkAreaPhysical = workAreaPhysical;
        _morphDpiScale = scale;
        _morphCustomX = customX;
        _morphCustomY = customY;
        _lastMorphWindowX = startX;

        if (!isSpringRunning || Math.Abs(targetLogicalWidth - _widthMorphTargetLogical) >= 0.5)
        {
            _widthSpringExpanding = targetLogicalWidth >= _widthSpringCurrentLogical;
        }

        _widthMorphTargetLogical = targetLogicalWidth;
        if (!isSpringRunning || Math.Abs(targetLogicalHeight - _heightMorphTargetLogical) >= 0.5)
        {
            _heightSpringExpanding = targetLogicalHeight >= _heightSpringCurrentLogical;
        }

        _heightMorphTargetLogical = targetLogicalHeight;
        _heightMorphPendingBounds = targetBounds;

        StartMorphAnimation();
    }

    private void StartMorphAnimation()
    {
        if (_morphAnimationRegistration is not null || _isWindowClosed)
        {
            return;
        }

        _heightSpringLastTimestamp = Stopwatch.GetTimestamp();
        _morphAnimationRegistration = WidgetCompactAnimationCoordinator.Register(
            OnMorphFrameRendered,
            _hwnd);
    }

    private void OnMorphFrameRendered()
    {
        if (!IsPopupVisible || _isWindowClosed || _appWindow is null)
        {
            StopHeightMorphTimer();
            return;
        }

        long nowTimestamp = Stopwatch.GetTimestamp();
        double dtSeconds = _heightSpringLastTimestamp > 0
            ? Stopwatch.GetElapsedTime(_heightSpringLastTimestamp, nowTimestamp).TotalSeconds
            : 0.014;
        _heightSpringLastTimestamp = nowTimestamp;

        SearchPopupWidthSpringStep widthStep = SearchPopupSurfacePolicy.StepWidthSpring(
            _widthSpringCurrentLogical,
            _widthSpringVelocity,
            _widthMorphTargetLogical,
            dtSeconds,
            _widthSpringExpanding);

        SearchPopupHeightSpringStep heightStep = SearchPopupSurfacePolicy.StepHeightSpring(
            _heightSpringCurrentLogical,
            _heightSpringVelocity,
            _heightMorphTargetLogical,
            dtSeconds,
            _heightSpringExpanding);

        _widthSpringCurrentLogical = widthStep.Width;
        _widthSpringVelocity = widthStep.Velocity;
        _heightSpringCurrentLogical = heightStep.Height;
        _heightSpringVelocity = heightStep.Velocity;

        if (widthStep.IsSettled && heightStep.IsSettled)
        {
            SnapMorphBoundsToTarget(
                _widthMorphTargetLogical,
                _heightMorphTargetLogical,
                _heightMorphPendingBounds,
                syncMinHeight: true);
            return;
        }

        if (Math.Abs(RootGrid.Width - widthStep.Width) > 0.001)
        {
            RootGrid.Width = widthStep.Width;
        }

        if (Math.Abs(RootGrid.Height - heightStep.Height) > 0.001)
        {
            RootGrid.Height = heightStep.Height;
        }

        int frameX = widthStep.IsSettled
            ? _heightMorphPendingBounds.X
            : SearchPopupSurfacePolicy.ComputeAnchoredWindowX(
                _morphWorkAreaPhysical,
                _morphDpiScale,
                widthStep.Width,
                _morphCustomX,
                _morphCustomY);
        if (frameX != _lastMorphWindowX)
        {
            _lastMorphWindowX = frameX;
            _ = Win32Helper.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                frameX,
                _heightMorphPendingBounds.Y,
                0,
                0,
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOZORDER |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_NOCOPYBITS);
        }
    }

    private void StopHeightMorphTimer()
    {
        if (_morphAnimationRegistration is { } registration)
        {
            _morphAnimationRegistration = null;
            try { registration.Dispose(); } catch { }
        }

        _heightSpringVelocity = 0;
        _widthSpringVelocity = 0;
        _heightSpringLastTimestamp = 0;
    }

    private void SnapHeightMorphToTarget()
    {
        if (!IsMorphSpringRunning || _appWindow is null)
        {
            return;
        }

        SnapMorphBoundsToTarget(
            _widthMorphTargetLogical,
            _heightMorphTargetLogical,
            _heightMorphPendingBounds,
            syncMinHeight: true);
    }

    private void SnapMorphBoundsToTarget(
        double targetLogicalWidth,
        double targetLogicalHeight,
        RectInt32 targetBounds,
        bool syncMinHeight = false)
    {
        StopHeightMorphTimer();
        _widthSpringCurrentLogical = targetLogicalWidth;
        _heightSpringCurrentLogical = targetLogicalHeight;
        RootGrid.HorizontalAlignment = HorizontalAlignment.Left;
        RootGrid.Width = targetLogicalWidth;
        RootGrid.Height = targetLogicalHeight;
        _lastMorphWindowX = targetBounds.X;
        if (syncMinHeight)
        {
            SyncResultsSurfaceMinHeight();
        }

        if (_appWindow is null)
        {
            return;
        }

        PointInt32 pos = _appWindow.Position;
        SizeInt32 size = _appWindow.Size;
        if (pos.X != targetBounds.X ||
            pos.Y != targetBounds.Y ||
            size.Width != targetBounds.Width ||
            size.Height != targetBounds.Height)
        {
            _appWindow.MoveAndResize(targetBounds);
        }
    }

    private void SearchBoxBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(SearchBoxBorder).Properties.IsLeftButtonPressed)
        {
            return;
        }

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (FindVisualAncestor<Button>(source) is not null ||
            FindVisualAncestor<TextBox>(source) is not null)
        {
            return;
        }

        if (SearchTextBox.Focus(FocusState.Programmatic))
        {
            int end = SearchTextBox.Text?.Length ?? 0;
            SearchTextBox.SelectionStart = end;
            SearchTextBox.SelectionLength = 0;
            e.Handled = true;
        }
    }

    private void SearchTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (SearchRecommendationLayoutEngine.FindVisualChild<Button>(SearchTextBox, "DeleteButton") is { } nativeDeleteButton)
        {
            nativeDeleteButton.MaxWidth = 0;
            nativeDeleteButton.MaxHeight = 0;
            nativeDeleteButton.Opacity = 0;
            nativeDeleteButton.IsHitTestVisible = false;
        }

        if (IsPopupVisible && !_isWindowClosed && _pendingInitialSearchBoxFocus)
        {
            _ = TryFocusSearchTextBoxForInput();
        }
    }

    private void ClearQueryButton_Click(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer?.Stop();
        _suppressScopeLatchTextChange = true;
        try
        {
            SearchTextBox.Text = string.Empty;
        }
        finally
        {
            _suppressScopeLatchTextChange = false;
        }

        ClearQueryButton.Visibility = Visibility.Collapsed;
        HotkeyHintBadge.Visibility = _latchedInlineScope == SearchInlineScope.None &&
                                     !string.IsNullOrWhiteSpace(_viewModel.HotkeyHint)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateScopePrefixBadge(string.Empty);
        if (_latchedInlineScope != SearchInlineScope.None)
        {
            _viewModel.Query = GetEffectivePopupQuery();
        }
        else
        {
            _viewModel.ClearSearch();
        }
        UpdatePanelVisibility();
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressScopeLatchTextChange)
        {
            return;
        }

        TryLatchInlineScopeFromInput(SearchTextBox.Text);

        // Typing a new query clears any recommended-app selection so that
        // Space/arrow keys switch back to result-list navigation.
        RecommendedAppsGrid.ClearSelection();
        _isNavigatingResults = false;
        ClearMultiSelection();
        bool hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
        if (HasActiveSearchOrScope)
        {
            RecommendedAppsGrid.DisableEditMode();
            RecommendedAppsGrid.CancelDrag();
            RecommendationOverlays.CloseOverlays(immediate: true);
        }
        ClearQueryButton.Visibility = hasText
            ? Visibility.Visible
            : Visibility.Collapsed;
        HotkeyHintBadge.Visibility = !hasText &&
                                     _latchedInlineScope == SearchInlineScope.None &&
                                     !string.IsNullOrWhiteSpace(_viewModel.HotkeyHint)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateScopePrefixBadge(SearchTextBox.Text);

        // When the input becomes empty outside active CJK IME preedit composition,
        // cancel any in-flight Everything IPC search immediately and return directly
        // to recommendations so a 30-80ms query cannot land during a 35ms debounce
        // and bounce Recommendation -> Search Results -> Recommendation.
        if (!HasActiveSearchOrScope)
        {
            _viewModel.CancelPendingSearch();
            if (!DeskBox.Helpers.ImeCompositionGuard.IsActivelyComposing(SearchTextBox))
            {
                _searchDebounceTimer?.Stop();
                _viewModel.ClearSearch();
                UpdatePanelVisibility();
                return;
            }
        }

        // Keep only a short coalescing window. Native queries are cancellable and the
        // resident catalog is the sole file provider, so a long visual debounce is
        // both unnecessary and directly visible as input latency.
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _searchDebounceTimer.Tick -= OnSearchDebounceTick;
        _searchDebounceTimer.Tick += OnSearchDebounceTick;
        _searchDebounceTimer.Start();
    }

    private void SearchTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _pendingInitialSearchBoxFocus = false;
        DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(SearchTextBox);
        HotkeyHintBadge.Opacity = 0.50;
        ApplySearchInputWellVisuals();
    }

    private void SearchTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        HotkeyHintBadge.Opacity = 0.68;
        ApplySearchInputWellVisuals();
    }

    private void OnSearchDebounceTick(object? sender, object e)
    {
        _searchDebounceTimer?.Stop();
        string effectiveQuery = GetEffectivePopupQuery();
        if (string.IsNullOrEmpty(effectiveQuery) && _latchedInlineScope == SearchInlineScope.None)
        {
            _viewModel.ClearSearch();
        }
        else
        {
            _viewModel.Query = effectiveQuery;
        }
        UpdatePanelVisibility();
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                if (_isManageTabsFlyoutOpen)
                {
                    ManageTabsFlyout.Hide();
                    e.Handled = true;
                    break;
                }
                if (_isCmdBarFlyoutOpen)
                {
                    CmdBarActionsFlyout.Hide();
                    e.Handled = true;
                    break;
                }
                if (HasOpenModalOrContextPopup())
                {
                    break;
                }
                if (RecommendationOverlays.IsEditingFolderTitle)
                {
                    RecommendationOverlays.CancelTitleEdit();
                }
                else if (RecommendationOverlays.IsAnyOverlayOpen)
                {
                    RecommendationOverlays.CloseOverlays();
                }
                else if (RecommendedAppsGrid.IsEditMode)
                {
                    if (RecommendedAppsGrid.MultiSelectedCount > 0)
                    {
                        RecommendedAppsGrid.ClearEditMultiSelection();
                    }
                    else
                    {
                        RecommendedAppsGrid.DisableEditMode();
                    }
                }
                else if (_multiSelectedItems.Count > 0)
                {
                    ClearMultiSelection();
                }
                else if (!string.IsNullOrEmpty(SearchTextBox.Text))
                {
                    SearchTextBox.Text = string.Empty;
                    if (_latchedInlineScope != SearchInlineScope.None)
                    {
                        _viewModel.Query = GetEffectivePopupQuery();
                        UpdatePanelVisibility();
                    }
                    else
                    {
                        _viewModel.ClearSearch();
                    }
                }
                else if (_latchedInlineScope != SearchInlineScope.None)
                {
                    ExitLatchedScope();
                }
                else
                {
                    HidePopup();
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                e.Handled = TryMoveResultSelection(e.Key);
                break;

            case Windows.System.VirtualKey.Down:
                e.Handled = TryMoveResultSelection(e.Key);
                break;

            case Windows.System.VirtualKey.Enter:
                if (RecommendationOverlays.IsAnyOverlayOpen)
                {
                    e.Handled = true;
                    break;
                }

                bool controlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
                if (!HasActiveSearchOrScope &&
                    RecommendedAppsGrid.IsAppsPanelVisible &&
                    RecommendedAppsGrid.HandleEnterKey(controlPressed))
                {
                    e.Handled = true;
                    break;
                }

                bool executed = controlPressed
                    ? ExecuteSecondaryCommandForSelectedItem()
                    : _viewModel.ExecuteSelectedItem();
                if (executed)
                {
                    HidePopup();
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Space:
                // After Up/Down navigation, plain Space triggers QuickLook preview.
                // Without navigation, Space types a space character (default behavior).
                if (_isNavigatingResults && _viewModel.SelectedItem is not null)
                {
                    TryPreviewSelectedItem();
                    e.Handled = true;
                }
                else if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
                {
                    TryPreviewSelectedItem();
                    e.Handled = true;
                }
                break;

            case Windows.System.VirtualKey.Tab:
                if (FocusSelectedResult())
                {
                    e.Handled = true;
                }
                break;
        }
    }

    private bool FocusSelectedResult()
    {
        if (_viewModel.SelectedItem is not { } selected ||
            FindRowByDataContext(ResultsRepeater, selected) is not { } row)
        {
            return false;
        }

        row.IsTabStop = true;
        return row.Focus(FocusState.Programmatic);
    }

    private void ResultsPanel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                if (_multiSelectedItems.Count > 0)
                {
                    ClearMultiSelection();
                }
                SearchTextBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                e.Handled = TryMoveResultSelection(e.Key);
                break;

            case Windows.System.VirtualKey.Down:
                e.Handled = TryMoveResultSelection(e.Key);
                break;

            case Windows.System.VirtualKey.Enter:
                bool opened = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
                    ? ExecuteSecondaryCommandForSelectedItem()
                    : _viewModel.ExecuteSelectedItem();
                if (opened)
                {
                    HidePopup();
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Space:
                TryPreviewSelectedItem();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.C when Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control):
                if (_multiSelectedItems.Count > 0)
                {
                    _ = CopySelectedItemsAsync(DataPackageOperation.Copy);
                    e.Handled = true;
                }
                break;

            case Windows.System.VirtualKey.X when Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control):
                if (_multiSelectedItems.Count > 0)
                {
                    _ = CopySelectedItemsAsync(DataPackageOperation.Move);
                    e.Handled = true;
                }
                break;

            case Windows.System.VirtualKey.Delete:
                if (_multiSelectedItems.Count > 0)
                {
                    _ = DeleteSelectedItemsAsync();
                    e.Handled = true;
                }
                break;

            case Windows.System.VirtualKey.A when Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control):
                // Ctrl+A: select all results.
                if (_viewModel.CurrentResults is { Count: > 0 } results)
                {
                    _multiSelectedItems.Clear();
                    foreach (var r in results)
                    {
                        _multiSelectedItems.Add(r);
                    }
                    _selectionAnchorIndex = Math.Max(0, _viewModel.SelectedIndex);
                    SyncMultiSelectionVisuals();
                    UpdateSelectionActions();
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>
    /// Redirects a WM_CHAR character received outside editable controls into
    /// the search text box without colliding with KeyDown/WM_CHAR double input.
    /// </summary>
    private bool TryRedirectCharacterToSearchBox(char ch)
    {
        if (ch == 0 || char.IsControl(ch) || ch == ' ')
        {
            return false;
        }

        SearchTextBox.Focus(FocusState.Programmatic);
        string currentText = SearchTextBox.Text ?? string.Empty;
        int start = Math.Clamp(SearchTextBox.SelectionStart, 0, currentText.Length);
        int length = Math.Clamp(SearchTextBox.SelectionLength, 0, currentText.Length - start);
        SearchTextBox.Text = string.Concat(
            currentText.AsSpan(0, start),
            new ReadOnlySpan<char>(in ch),
            currentText.AsSpan(start + length));
        SearchTextBox.SelectionStart = start + 1;
        SearchTextBox.SelectionLength = 0;
        return true;
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.InvokeAction("open-settings");
    }

    // ── Recommended apps: Win11 Start Menu single-click open, drag reorder, folder grouping & batch edit mode ──

    private void EditRecommendationsButton_Click(object sender, RoutedEventArgs e)
    {
        if (RecommendationOverlays.IsAnyOverlayOpen)
        {
            RecommendationOverlays.CloseOverlays();
        }

        RecommendedAppsGrid.ToggleEditMode();
    }

    private void SyncEditRecommendationsButtonVisuals()
    {
        bool isEditMode = RecommendedAppsGrid.IsEditMode;
        EditRecommendationsIcon.Glyph = isEditMode ? "\uE73E" : "\uE70F";
        string label = _localizationService.T(
            isEditMode
                ? "Search.Recommend.EditDone"
                : "Search.Recommend.Edit");
        EditRecommendationsLabel.Text = label;
        ToolTipService.SetToolTip(EditRecommendationsButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(EditRecommendationsButton, label);
        EditRecommendationsButton.Background = SearchRecommendationLayoutEngine.ResolveThemeBrush(
            RootGrid,
            isEditMode
                ? "ControlFillColorSecondaryBrush"
                : "ControlFillColorDefaultBrush");
        EditRecommendationsButton.BorderBrush = SearchRecommendationLayoutEngine.ResolveThemeBrush(
            RootGrid,
            isEditMode
                ? "ControlStrokeColorDefaultBrush"
                : "CardStrokeColorDefaultBrush");
    }

    private void UpdateRecommendationEditStatusHint()
    {
        int selectedCount = RecommendedAppsGrid.MultiSelectedCount;
        bool showHint = string.IsNullOrWhiteSpace(SearchTextBox.Text) &&
                        RecommendedAppsGrid.IsAppsPanelVisible &&
                        RecommendedAppsGrid.IsEditMode &&
                        selectedCount > 0;
        if (showHint)
        {
            string batchHideLabel = string.Format(
                _localizationService.T("Search.Recommend.BatchHideFormat"),
                selectedCount);
            BatchHideSelectedLabel.Text = batchHideLabel;
            ToolTipService.SetToolTip(BatchHideSelectedButton, batchHideLabel);
            BatchHideSelectedButton.Visibility = Visibility.Visible;

            StatusTextBlock.Text = string.Format(
                _localizationService.T("Search.Recommend.EditSelectionHint"),
                selectedCount);
            StatusBar.Visibility = Visibility.Visible;
        }
        else
        {
            BatchHideSelectedButton.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
        }
    }

    private void BatchHideSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        RecommendedAppsGrid.HideSelected();
    }

    private void HiddenAppsEntryButton_Click(object sender, RoutedEventArgs e)
    {
        RecommendationOverlays.OpenHiddenApps();
    }

    private void OnLanguageChanged()
    {
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            SetupBindings();
            _viewModel.RebuildTabsPublic();
        }))
        {
            // Queue not available (window closing), ignore
        }
    }

    // ── Tab bar ─────────────────────────────────────────────────────────────

    private void TabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabsList.SelectedItem is SearchTabItem tab &&
            !ReferenceEquals(tab, _viewModel.SelectedTab))
        {
            _viewModel.SelectedTab = tab;
        }
    }

    private void SyncTabSelection()
    {
        if (!ReferenceEquals(TabsList.SelectedItem, _viewModel.SelectedTab))
        {
            TabsList.SelectedItem = _viewModel.SelectedTab;
        }
        ScrollSelectedTabIntoView();
        ClearMultiSelection();
    }

    // Result row interaction (hover, click, drag, and context menu).

    private void OnViewModelContentRequested(object? sender, SearchResultItem item)
    {
        HidePopup();
        ContentRequested?.Invoke(this, item);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_isWindowClosed)
        {
            return;
        }

        _isWindowClosed = true;
        IsPopupVisible = false;
        App.Log(
            $"[Search] Popup window closed visible={IsPopupVisible} " +
            $"hwnd=0x{_hwnd.ToInt64():X}");
        RemovePopupCloseWatcherSubclass();
        Closed -= OnWindowClosed;
        _viewModel.ActionRequested -= OnViewModelActionRequested;
        _viewModel.ContentRequested -= OnViewModelContentRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.CurrentResults.CollectionChanged -= OnCurrentResultsCollectionChanged;
        ResultsRepeater.ElementPrepared -= OnResultsElementPrepared;
        ResultsRepeater.ElementClearing -= OnResultsElementClearing;

        _viewModel.RecommendationIconsResolved -= OnRecommendationIconsResolved;
        _viewModel.RecommendationLayoutUpdated -= OnRecommendationLayoutUpdated;
        _settingsService.SettingsChanged -= OnAppearanceSettingsChanged;
        _settingsService.AppearancePreviewChanged -= OnAppearanceSettingsChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        if (_themeService is not null)
            _themeService.AppearanceChanged -= OnThemeServiceAppearanceChanged;
        Activated -= OnWindowActivated;
        try
        {
            PopupShowStoryboard.Stop();
            PopupHideStoryboard.Stop();
        }
        catch
        {
        }
        PopupHideStoryboard.Completed -= OnPopupHideCompleted;
        if (_searchDebounceTimer is { } debounceTimer)
        {
            _searchDebounceTimer = null;
            debounceTimer.Tick -= OnSearchDebounceTick;
            try
            {
                debounceTimer.Stop();
            }
            catch
            {
            }
        }

        SearchFeedbackPresenter.Clear();
        if (_rubberBandAutoScrollTimer is { } autoScrollTimer)
        {
            _rubberBandAutoScrollTimer = null;
            autoScrollTimer.Tick -= OnRubberBandAutoScrollTick;
            try
            {
                autoScrollTimer.Stop();
            }
            catch
            {
            }
        }

        DisposeUnfocusedAutoHideTimer();
        DisposeColdHiddenCleanupTimer();
        DisposeExternalDragFadeTimer();
        StopHeightMorphTimer();
        CancelPendingDetailsFilePreview();
        _detailsEntranceStoryboard?.Stop(); _detailsEntranceStoryboard = null;
        _detailsUpdateCoalesceTimer?.Stop(); _detailsUpdateCoalesceTimer = null;
        ClearSelectedResultRow();
        _multiSelectedItems.Clear();
        _rubberBandBaseSelection.Clear();
        ClearRealizedRepeaterVisuals();
        RecommendedAppsGrid.Dispose();
        RecommendationOverlays.Dispose();
        ResultsRepeater.ItemsSource = null;
        TabsList.ItemsSource = null;
        DisposeAcrylicController();
        DisposeMicaController();
        ClearTransparentWindowBackdrop();
        _materialBackdropBridge?.Dispose();
        _materialBackdropBridge = null;
        ActionRequested = null;
        ContentRequested = null;
        PopupShown = null;
        PopupHidden = null;
        PopupDeactivated = null;
        SearchEngineService.ReleaseActiveColdHiddenCaches();
        _viewModel.Dispose();
        Bindings?.StopTracking();
        RootGrid.Children.Clear();
        Content = null;
    }

    private static bool AreSystemAnimationsEnabled() =>
        WindowsCompatibilityService.ShouldAnimate;

    // Host contracts for extracted recommendation grid & overlay controls
    bool ISearchRecommendationGridHost.IsPopupVisible => IsPopupVisible;
    bool ISearchRecommendationGridHost.IsWindowClosed => _isWindowClosed;
    bool ISearchRecommendationGridHost.IsDragOutInProgress => _dragOutInProgress;
    FrameworkElement ISearchRecommendationGridHost.PopupBoundsRoot => RootGrid;
    bool ISearchRecommendationGridHost.IsAnyOverlayOpen => RecommendationOverlays.IsAnyOverlayOpen;
    void ISearchRecommendationGridHost.HidePopup() => HidePopup();
    void ISearchRecommendationGridHost.FocusSearchBox() => SearchTextBox.Focus(FocusState.Programmatic);
    void ISearchRecommendationGridHost.FocusPopupRoot() => SearchTextBox.Focus(FocusState.Programmatic);
    void ISearchRecommendationGridHost.OpenFolderOverlay(RecommendedGridEntry folderEntry, bool startRename) =>
        RecommendationOverlays.OpenFolder(folderEntry, startRename);
    void ISearchRecommendationGridHost.CloseOverlays(bool immediate) =>
        RecommendationOverlays.CloseOverlays(immediate);
    void ISearchRecommendationGridHost.CancelOverlayDrag() =>
        RecommendationOverlays.CancelDrag();
    void ISearchRecommendationGridHost.RefreshOverlayIcons() =>
        RecommendationOverlays.RefreshIcons();
    void ISearchRecommendationGridHost.UpdateRecommendationHeaderState() =>
        UpdateRecommendationHeaderState();
    void ISearchRecommendationGridHost.SyncEditModeChrome(bool isEditMode, int multiSelectedCount)
    {
        SyncEditRecommendationsButtonVisuals();
        UpdateRecommendationEditStatusHint();
    }
    void ISearchRecommendationGridHost.TryPreviewSelectedItem() => TryPreviewSelectedItem();
    bool ISearchRecommendationGridHost.TryStartRecommendedExternalDragOut(
        UIElement dragSource,
        PointerRoutedEventArgs e,
        IReadOnlyList<string> candidatePaths,
        string originTag,
        Action suspendInternalDrag) =>
        TryStartRecommendedExternalDragOut(dragSource, e, candidatePaths, originTag, suspendInternalDrag);
    void ISearchRecommendationGridHost.QueueRunItemAsAdministrator(SearchResultItem item) =>
        QueueRunItemAsAdministrator(item, hideOnLaunch: true);

    bool ISearchRecommendationOverlayHost.IsRecommendationEditMode => RecommendedAppsGrid.IsEditMode;
    bool ISearchRecommendationOverlayHost.IsDragOutInProgress => _dragOutInProgress;
    bool ISearchRecommendationOverlayHost.IsPopupVisible => IsPopupVisible;
    bool ISearchRecommendationOverlayHost.IsWindowClosed => _isWindowClosed;
    IntPtr ISearchRecommendationOverlayHost.OwnerWindowHandle => _hwnd;
    FrameworkElement ISearchRecommendationOverlayHost.PopupBoundsRoot => RootGrid;
    FrameworkElement ISearchRecommendationOverlayHost.SearchHeaderElement => SearchBoxRow;
    void ISearchRecommendationOverlayHost.HidePopup() => HidePopup();
    void ISearchRecommendationOverlayHost.FocusSearchBox() => SearchTextBox.Focus(FocusState.Programmatic);
    void ISearchRecommendationOverlayHost.FocusPopupRoot() => SearchTextBox.Focus(FocusState.Programmatic);
    void ISearchRecommendationOverlayHost.CancelRecommendedGridDrag() => RecommendedAppsGrid.CancelDrag();
    void ISearchRecommendationOverlayHost.EndRecommendedGridRubberBand() => RecommendedAppsGrid.EndRubberBand();
    void ISearchRecommendationOverlayHost.UpdatePopupDynamicBounds() => UpdatePopupDynamicBounds();
    bool ISearchRecommendationOverlayHost.TryStartRecommendedExternalDragOut(
        UIElement dragSource,
        PointerRoutedEventArgs e,
        IReadOnlyList<string> candidatePaths,
        string originTag,
        Action suspendInternalDrag) =>
        TryStartRecommendedExternalDragOut(dragSource, e, candidatePaths, originTag, suspendInternalDrag);
    void ISearchRecommendationOverlayHost.AppendCommonRecommendedAppActions(MenuFlyout flyout, SearchResultItem item) =>
        RecommendedAppsGrid.AppendCommonRecommendedAppActions(flyout, item);

    // Pure geometry / projection forwarders preserved for unit tests
    internal const double DropAnchorDeadband = SearchRecommendationLayoutEngine.DropAnchorDeadband;
    internal static Rect InflateDropAnchorRect(Rect anchor, double margin) =>
        SearchRecommendationLayoutEngine.InflateDropAnchorRect(anchor, margin);
    internal static Rect ComputeTileMergeAnchorRect(Rect tileRect, bool targetIsFolder) =>
        SearchRecommendationLayoutEngine.ComputeTileMergeAnchorRect(tileRect, targetIsFolder);
    internal static Rect ComputeTileReorderAnchorRect(
        Rect tileRect,
        double relX,
        double relY,
        bool beforeTile,
        bool canMergeOntoTarget,
        bool targetIsFolder) =>
        SearchRecommendationLayoutEngine.ComputeTileReorderAnchorRect(
            tileRect,
            relX,
            relY,
            beforeTile,
            canMergeOntoTarget,
            targetIsFolder);
    internal static Rect ComputeTrailingAppendAnchorRect(
        Point pointInSurface,
        Rect lastTileRect,
        double surfaceWidth,
        double surfaceHeight) =>
        SearchRecommendationLayoutEngine.ComputeTrailingAppendAnchorRect(
            pointInSurface,
            lastTileRect,
            surfaceWidth,
            surfaceHeight);
    internal static bool IsPointLeavingPopupDragBounds(
        Point pointInRoot,
        double rootWidth,
        double rootHeight) =>
        SearchRecommendationLayoutEngine.IsPointLeavingPopupDragBounds(
            pointInRoot,
            rootWidth,
            rootHeight);
    internal static bool WouldBatchReorderChangeOrder(
        IReadOnlyList<RecommendedGridEntry> entries,
        IReadOnlySet<string> draggedEntryKeys,
        int targetVisibleIndex) =>
        SearchRecommendationLayoutEngine.WouldBatchReorderChangeOrder(
            entries,
            draggedEntryKeys,
            targetVisibleIndex);
    internal static List<string> ProjectReorderedEntryKeys(
        IReadOnlyList<RecommendedGridEntry> entries,
        IReadOnlySet<string> draggedEntryKeys,
        int insertIndexInRemaining) =>
        SearchRecommendationLayoutEngine.ProjectReorderedEntryKeys(
            entries,
            draggedEntryKeys,
            insertIndexInRemaining);
    internal static bool ShouldMergeOntoTile(
        double relX,
        double relY,
        bool targetIsFolder,
        bool isBatchDrag) =>
        SearchRecommendationLayoutEngine.ShouldMergeOntoTile(
            relX,
            relY,
            targetIsFolder,
            isBatchDrag);
}

// ────────────────────────────────────────────────────────────────────────────

