using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Window-independent Quick Capture member. All top-level window, DWM,
/// z-order, bounds, capsule, and group navigation behavior stays with the
/// surface host; this control owns only Quick Capture data and interaction.
/// </summary>
public sealed partial class QuickCaptureSurfaceContent :
    UserControl,
    IWidgetContent,
    IWidgetFeedbackSource,
    IWidgetTransientStateContent,
    IWidgetResponsiveLayoutContent,
    IWidgetHostViewportContent,
    IWidgetInteractiveResizeContent,
    IWidgetAddActionContent,
    IWidgetGroupContentCacheable,
    IDisposable
{
    private const string MasterPaneWidthMetadataKey = "QuickCaptureMasterPaneWidth";
    private const int DetailAutoSaveDelayMs = 600;
    private const int DetailImageDecodePixelWidth = 1200;
    private readonly LocalizationService _localizationService;
    private readonly SettingsService _settingsService;
    private readonly MasterDetailLayoutPolicy _masterDetailLayoutPolicy = new();
    private readonly MarkdownDocumentService _markdownDocumentService = new();
    private readonly List<DroppedFilePath> _pendingDetailAttachments = [];
    private readonly SemaphoreSlim _detailSaveGate = new(1, 1);
    private DispatcherQueueTimer? _detailAutoSaveTimer;
    private string _lastFocusTarget = "Root";
    private string? _pendingFocusTarget;
    private string? _pendingDetailItemId;
    private bool _pendingDetailEditing;
    private string? _pendingDetailDraft;
    private bool _pendingDetailWasVisibleInSinglePane;
    private QuickCaptureItemViewModel[] _pendingPointerDragItems = [];
    private string? _draggedQuickCaptureItemId;
    private readonly List<string> _draggedQuickCaptureItemIds = [];
    private bool _isInternalQuickCaptureDrag;
    private bool _internalQuickCaptureDragCanReorder;
    private QuickCaptureViewMode? _internalQuickCaptureDragView;
    private bool _isDualPane;
    private bool _showDetailInSinglePane;
    private bool _isDetailEditing;
    private bool _isCreatingDetail;
    private bool _suppressDetailEditorChanges;
    private bool _detailHasUnsavedChanges;
    private bool _isSavingDetail;
    private long _detailEditRevision;
    private long _detailSavedRevision;
    private long _detailImageLoadVersion;
    private string? _detailPrimaryImagePath;
    private string? _detailAttachmentRenderKey;
    private bool _isSynchronizingViewSelection;
    private long _viewSwitchRevision;
    private QuickCaptureItemViewModel? _detailItem;
    private QuickCaptureAppearancePreset _detailAppearance;
    private TextContentFormat _detailContentFormat = TextContentFormat.Markdown;
    private double? _persistedMasterPaneWidth;
    private double _hostViewportWidth = double.NaN;
    private EventHandler<object>? _segmentedRestoreHandler;
    private int _segmentedStableFrames;
    private double _segmentedCandidateWidth;
    private bool _isResponsiveLayoutTransitionActive;
    private bool _isInteractiveResizeActive;
    private bool _deferDetailReaderUntilTransitionCompletes;
    private bool _isDisposed;
    private bool _isInitialized;
    private bool _isWindowVisible;
    private bool _isWindowRevealCompleted;

    public QuickCaptureSurfaceContent(
        WidgetConfig config,
        QuickCaptureService quickCaptureService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(quickCaptureService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(dispatcherQueue);

        _localizationService = localizationService;
        _settingsService = settingsService;
        ViewModel = new QuickCaptureWidgetViewModel(
            config,
            quickCaptureService,
            settingsService,
            localizationService,
            dispatcherQueue);

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            string exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
            App.Log(
                $"[QuickCaptureSurface] XAML initialization failed: " +
                $"Type={exceptionType}, HResult=0x{ex.HResult:X8}, Message={ex.Message}, " +
                $"InnerException={ex.InnerException}, StackTrace={ex.StackTrace}");
            throw;
        }
        ResponsiveContentGrid.DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        if (config.Metadata.TryGetValue(MasterPaneWidthMetadataKey, out string? persisted) &&
            double.TryParse(persisted, NumberStyles.Float, CultureInfo.InvariantCulture, out double width))
        {
            _persistedMasterPaneWidth = _masterDetailLayoutPolicy.NormalizePersistedMasterWidth(width);
        }
        DetailMarkdownEditor.TextResolver = localizationService.T;
        DetailMarkdownEditor.EditorTextChanged += DetailMarkdownEditor_EditorTextChanged;
        DetailMarkdownEditor.TextTruncated += DetailMarkdownEditor_TextTruncated;
        DetailMarkdownEditor.CommitRequested += DetailMarkdownEditor_CommitRequested;
        DetailMarkdownView.AttachmentResolver = ResolveDetailAttachmentPath;
        DetailMarkdownView.AttachmentOpenRequested += DetailMarkdownView_AttachmentOpenRequested;
        DetailBodyReaderSurface.AddHandler(
            UIElement.DoubleTappedEvent,
            new DoubleTappedEventHandler(DetailBodyReaderSurface_DoubleTapped),
            handledEventsToo: true);
        _detailAutoSaveTimer = dispatcherQueue.CreateTimer();
        _detailAutoSaveTimer.Interval = TimeSpan.FromMilliseconds(DetailAutoSaveDelayMs);
        _detailAutoSaveTimer.IsRepeating = false;
        _detailAutoSaveTimer.Tick += DetailAutoSaveTimer_Tick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += QuickCaptureSurfaceContent_ActualThemeChanged;
        UpdateSelectedViewVisual();
    }

    public QuickCaptureWidgetViewModel ViewModel { get; }

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public WidgetConfig Config => ViewModel.Config;

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => WidgetKind.QuickCapture;

    public FrameworkElement View => this;

    public bool IsReadyForReuse => _isInitialized && !_isDisposed;

    public async Task InitializeAsync()
    {
        await ViewModel.InitializeAsync();
        _isInitialized = true;
        UpdateSelectedViewVisual();
        ApplyResponsiveLayout();
        if (!RestorePendingDetailState())
        {
            ReconcileDetailSelection();
        }
    }

    public Task RefreshAsync() => ViewModel.RefreshItemsAsync();

    public async Task AddFromTitleButtonAsync()
    {
        await OpenNewDetailAsync();
    }

    internal async Task RevealItemAsync(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        ViewModel.CollapseSearch();
        ViewModel.SelectedView = QuickCaptureViewMode.Records;
        await ViewModel.RefreshItemsAsync();
        QuickCaptureItemViewModel? item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
        await OpenDetailAfterSavingAsync(item);
    }

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearancePreview();
        UpdateSelectedViewVisual();
        ApplySegmentedStyle();
        ApplyResponsiveLayout();
    }

    public void OnActivated()
    {
        if (IsLoaded)
        {
            ApplyPendingFocus();
        }
    }

    public async void OnDeactivated()
    {
        _lastFocusTarget = GetCurrentFocusTarget();
        await FlushPendingDetailSaveAsync();
    }

    public async void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (!visible)
        {
            _isWindowRevealCompleted = false;
            ViewModel.SuspendWindowRefresh();
            await FlushPendingDetailSaveAsync();
        }
    }

    public void OnWindowRevealCompleted()
    {
        if (!_isWindowVisible || _isWindowRevealCompleted)
        {
            return;
        }

        _isWindowRevealCompleted = true;
        if (IsLoaded)
        {
            ViewModel.RefreshAfterViewReady();
        }
    }

    public void RestoreTransientState(string? inputText, string? searchText)
    {
        ViewModel.InputText = inputText ?? string.Empty;
        ViewModel.SearchText = searchText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            ViewModel.ExpandSearch();
        }
    }

    object? IWidgetTransientStateContent.CaptureTransientState()
    {
        bool shouldCaptureDetail =
            QuickCaptureDetailRestorePolicy.ShouldCaptureDetail(
                _isDualPane,
                _showDetailInSinglePane,
                _detailItem is not null);
        return new QuickCaptureWidgetTransientState(
            ViewModel.InputText,
            ViewModel.SearchText,
            ViewModel.SelectedView,
            _lastFocusTarget,
            shouldCaptureDetail ? _detailItem?.Id : null,
            shouldCaptureDetail && _isDetailEditing,
            shouldCaptureDetail && _isDetailEditing
                ? DetailMarkdownEditor.Text
                : null,
            shouldCaptureDetail && !_isDualPane && _showDetailInSinglePane);
    }

    void IWidgetTransientStateContent.RestoreTransientState(object? state)
    {
        if (state is QuickCaptureWidgetTransientState quickState)
        {
            RestoreTransientState(
                quickState.InputText,
                quickState.SearchText);
            ViewModel.RestoreSelectedViewImmediately(quickState.SelectedView);
            _pendingFocusTarget = quickState.FocusTarget;
            _pendingDetailItemId = quickState.SelectedDetailItemId;
            _pendingDetailEditing = quickState.IsDetailEditing;
            _pendingDetailDraft = quickState.DetailDraft;
            _pendingDetailWasVisibleInSinglePane =
                quickState.WasDetailVisibleInSinglePane;
            UpdateSelectedViewVisual();
            if (IsLoaded && _isInitialized)
            {
                RestorePendingDetailState();
                DispatcherQueue.TryEnqueue(ApplyPendingFocus);
            }
        }
    }

    private bool RestorePendingDetailState()
    {
        if (string.IsNullOrWhiteSpace(_pendingDetailItemId))
        {
            _pendingDetailWasVisibleInSinglePane = false;
            return false;
        }

        if (!QuickCaptureDetailRestorePolicy.ShouldRestoreDetail(
                _isDualPane,
                _pendingDetailWasVisibleInSinglePane))
        {
            _pendingDetailItemId = null;
            _pendingDetailEditing = false;
            _pendingDetailDraft = null;
            _pendingDetailWasVisibleInSinglePane = false;
            return false;
        }

        QuickCaptureItemViewModel? item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _pendingDetailItemId, StringComparison.Ordinal));
        _pendingDetailItemId = null;
        _pendingDetailWasVisibleInSinglePane = false;
        if (item is null)
        {
            _pendingDetailEditing = false;
            _pendingDetailDraft = null;
            return false;
        }

        OpenDetail(item);
        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
        if (_pendingDetailEditing && !item.IsRecent)
        {
            BeginDetailEditing();
            if (_pendingDetailDraft is { } draft &&
                !string.Equals(draft, item.Body, StringComparison.Ordinal))
            {
                SetDetailEditorText(draft);
                MarkDetailDirty();
                _detailAutoSaveTimer?.Start();
            }
            RefreshDetailPresentation();
        }

        _pendingDetailEditing = false;
        _pendingDetailDraft = null;
        return true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isWindowRevealCompleted)
        {
            ViewModel.RefreshAfterViewReady();
        }
        UpdateSelectedViewVisual();
        ApplyPendingFocus();
        ApplyResponsiveLayout();
        if (_isInitialized && !_isCreatingDetail)
        {
            ReconcileDetailSelection();
        }
        RefreshItemMaterialSurfaces();
        QueueSegmentedRestore();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A cached group member can be detached before its former host settles
        // a responsive transition. Those flags only describe the old visual
        // attachment and must not keep the reader hidden when it is reattached.
        CancelSegmentedRestore();
        _isResponsiveLayoutTransitionActive = false;
        _isInteractiveResizeActive = false;
        _deferDetailReaderUntilTransitionCompletes = false;
    }

    private void ResponsiveContentGrid_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (!_isInteractiveResizeActive)
        {
            ApplyResponsiveLayout();
        }
    }

    public void BeginInteractiveResize(double contentWidth, double contentHeight)
    {
        _isInteractiveResizeActive = true;
        CancelSegmentedRestore();
        _deferDetailReaderUntilTransitionCompletes = true;
    }

    public void CompleteInteractiveResize(double contentWidth, double contentHeight)
    {
        _isInteractiveResizeActive = false;
        _deferDetailReaderUntilTransitionCompletes = false;
        if (double.IsFinite(contentWidth) && contentWidth > 0)
        {
            _hostViewportWidth = contentWidth;
            Width = contentWidth;
        }

        ApplyResponsiveLayout();
        RefreshDetailPresentation();
        QueueSegmentedRestore();
    }

    private void ApplyResponsiveLayout()
    {
        if (ResponsiveContentGrid is null ||
            MasterColumn is null ||
            SplitterColumn is null ||
            DetailColumn is null ||
            ListPage is null ||
            DetailPage is null ||
            PaneSplitter is null)
        {
            return;
        }

        double layoutWidth = double.IsFinite(_hostViewportWidth) &&
                             _hostViewportWidth > 0
            ? _hostViewportWidth
            : ResponsiveContentGrid.ActualWidth;
        double availableWidth = Math.Max(
            0,
            layoutWidth -
            ResponsiveContentGrid.Padding.Left -
            ResponsiveContentGrid.Padding.Right);
        string preference = SettingsService.NormalizeQuickCaptureWideLayout(
            _settingsService.Settings.QuickCaptureWideLayout);
        bool forceSinglePane =
            preference == SettingsService.QuickCaptureWideLayoutSinglePane;
        bool forceDualPane =
            preference == SettingsService.QuickCaptureWideLayoutDualPane;
        MasterDetailLayoutSnapshot layout = _masterDetailLayoutPolicy.Resolve(
            availableWidth,
            _isDualPane,
            _persistedMasterPaneWidth,
            forceSinglePane,
            forceDualPane);
        bool enteredDualPane = !_isDualPane && layout.IsDualPane;
        _isDualPane = layout.IsDualPane;

        if (_isDualPane)
        {
            // Keep the grid itself shrinkable. Pixel minimums on the columns
            // become part of the control's desired size and can leave the
            // surface measuring at the normal 588 epx dual-pane width after
            // its host has already become narrower. The policy below still
            // protects both panes at normal widths and proportionally
            // compresses them when DualPane is explicitly selected.
            MasterColumn.MinWidth = 0;
            DetailColumn.MinWidth = 0;
            MasterColumn.Width = new GridLength(layout.MasterWidth);
            SplitterColumn.Width = new GridLength(layout.SplitterWidth);
            DetailColumn.Width = new GridLength(layout.DetailWidth);
            ListPage.Visibility = Visibility.Visible;
            PaneSplitter.Visibility = Visibility.Visible;
            DetailPage.Visibility = Visibility.Visible;
            DetailBackButton.Visibility = Visibility.Collapsed;
            DetailBackColumn.Width = new GridLength(8);
            _showDetailInSinglePane = false;
            if (enteredDualPane)
            {
                ReconcileDetailSelection();
            }
        }
        else
        {
            MasterColumn.MinWidth = 0;
            DetailColumn.MinWidth = 0;
            SplitterColumn.Width = new GridLength(0);
            PaneSplitter.Visibility = Visibility.Collapsed;
            if (_showDetailInSinglePane)
            {
                MasterColumn.Width = new GridLength(0);
                DetailColumn.Width = new GridLength(1, GridUnitType.Star);
                ListPage.Visibility = Visibility.Collapsed;
                DetailPage.Visibility = Visibility.Visible;
            }
            else
            {
                MasterColumn.Width = new GridLength(1, GridUnitType.Star);
                DetailColumn.Width = new GridLength(0);
                ListPage.Visibility = Visibility.Visible;
                DetailPage.Visibility = Visibility.Collapsed;
            }
            DetailBackButton.Visibility = Visibility.Visible;
            DetailBackColumn.Width = new GridLength(28);
        }

        RefreshDetailPresentation();
        SynchronizeSegmentedVisibility();
    }

    private void ReconcileDetailSelection()
    {
        if (!_isDualPane)
        {
            return;
        }

        if (_detailItem is not null)
        {
            QuickCaptureItemViewModel? refreshed = ViewModel.Items.FirstOrDefault(
                item => item.Id == _detailItem.Id);
            if (refreshed is not null)
            {
                // Cached group members are detached and reattached without
                // changing their item instances. Reopening the same detail
                // would rebuild attachment projections and, for image notes,
                // clear and decode the same 1200px preview again.
                if (ReferenceEquals(refreshed, _detailItem))
                {
                    return;
                }

                if (_isDetailEditing || _detailHasUnsavedChanges)
                {
                    _detailItem = refreshed;
                    foreach (QuickCaptureItemViewModel candidate in ViewModel.Items)
                    {
                        candidate.IsDetailSelected = candidate.Id == refreshed.Id;
                    }
                    RefreshDetailPresentation();
                    return;
                }
                OpenDetail(refreshed);
                return;
            }
        }

        QuickCaptureItemViewModel? selected =
            ViewModel.Items.FirstOrDefault(item => item.IsDetailSelected);
        if (selected is null &&
            ItemsList.SelectedItem is QuickCaptureItemViewModel listSelection)
        {
            selected = ViewModel.Items.FirstOrDefault(item =>
                string.Equals(item.Id, listSelection.Id, StringComparison.Ordinal));
        }

        selected ??= ViewModel.Items.FirstOrDefault();
        if (selected is not null)
        {
            OpenDetail(selected);
        }
        else
        {
            _detailItem = null;
            RefreshDetailPresentation();
        }
    }

    private void PaneSplitter_ManipulationCompleted(
        object sender,
        ManipulationCompletedRoutedEventArgs e)
    {
        PersistMasterPaneWidth();
        ApplyResponsiveLayout();
    }

    private void PaneSplitter_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        _persistedMasterPaneWidth = _masterDetailLayoutPolicy.Options.DefaultMasterWidth;
        PersistMasterPaneWidth();
        ApplyResponsiveLayout();
        e.Handled = true;
    }

    private void PersistMasterPaneWidth()
    {
        if (!_isDualPane || !double.IsFinite(MasterColumn.ActualWidth))
        {
            return;
        }

        double masterWidth = MasterColumn.ActualWidth;
        double minimumDualWidth =
            _masterDetailLayoutPolicy.Options.MinimumMasterWidth +
            _masterDetailLayoutPolicy.Options.SplitterWidth +
            _masterDetailLayoutPolicy.Options.MinimumDetailWidth;
        double layoutWidth = double.IsFinite(_hostViewportWidth) &&
                             _hostViewportWidth > 0
            ? _hostViewportWidth
            : ResponsiveContentGrid.ActualWidth;
        double availableWidth = Math.Max(
            0,
            layoutWidth -
            ResponsiveContentGrid.Padding.Left -
            ResponsiveContentGrid.Padding.Right);
        string preference = SettingsService.NormalizeQuickCaptureWideLayout(
            _settingsService.Settings.QuickCaptureWideLayout);
        if (preference == SettingsService.QuickCaptureWideLayoutDualPane &&
            availableWidth < minimumDualWidth)
        {
            double combinedPaneWidth =
                MasterColumn.ActualWidth + DetailColumn.ActualWidth;
            if (combinedPaneWidth > 0)
            {
                double masterRatio = Math.Clamp(
                    MasterColumn.ActualWidth / combinedPaneWidth,
                    0.01,
                    0.99);
                masterWidth = _masterDetailLayoutPolicy.Options.MinimumDetailWidth *
                              masterRatio /
                              (1 - masterRatio);
            }
        }

        _persistedMasterPaneWidth =
            _masterDetailLayoutPolicy.NormalizePersistedMasterWidth(masterWidth);
        Config.Metadata[MasterPaneWidthMetadataKey] =
            _persistedMasterPaneWidth.Value.ToString(
                "0.###",
                CultureInfo.InvariantCulture);
        _settingsService.SaveDebounced();
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        _isResponsiveLayoutTransitionActive = true;
        CancelSegmentedRestore();

        // Expansion reveals the live body while the HWND grows. Match the
        // final expanded layout before the first animation frame, just like
        // Search, Music, Weather and Todo, so a capsule-width master/detail
        // surface is never stretched through the intermediate bounds.
        if (!isCollapsing &&
            double.IsFinite(targetContentWidth) &&
            targetContentWidth > 0)
        {
            _deferDetailReaderUntilTransitionCompletes = true;
            _hostViewportWidth = targetContentWidth;
            Width = targetContentWidth;
            ApplyResponsiveLayout();
            PrepareSegmentedForExpansion(targetContentWidth);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.TabBarVisibility))
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                SynchronizeSegmentedVisibility();
            }
            else
            {
                DispatcherQueue.TryEnqueue(SynchronizeSegmentedVisibility);
            }
            return;
        }

        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.EditorContentFormat))
        {
            if (_isDetailEditing && _detailItem?.IsRecent != true)
            {
                _detailContentFormat = ViewModel.EditorContentFormat;
                RefreshDetailPresentation();
            }
            return;
        }

        if (e.PropertyName != nameof(QuickCaptureWidgetViewModel.ItemsViewTransitionToken))
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            ReconcileDetailSelection();
            DispatcherQueue.TryEnqueue(RefreshItemMaterialSurfaces);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                ReconcileDetailSelection();
                RefreshItemMaterialSurfaces();
            }
        });
    }

    public void OnHostViewportSizeChanged(double width, double height)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return;
        }

        _hostViewportWidth = width;
        Width = width;
        if (!_isInteractiveResizeActive)
        {
            ApplyResponsiveLayout();
        }
    }

    public void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        _isResponsiveLayoutTransitionActive = false;
        _deferDetailReaderUntilTransitionCompletes = false;
        if (double.IsFinite(finalContentWidth) && finalContentWidth > 0)
        {
            _hostViewportWidth = finalContentWidth;
            Width = finalContentWidth;
        }

        ApplyResponsiveLayout();
        QueueSegmentedRestore();
    }

    public void CancelResponsiveLayoutTransition()
    {
        _isResponsiveLayoutTransitionActive = false;
        _deferDetailReaderUntilTransitionCompletes = false;
        ApplyResponsiveLayout();
        QueueSegmentedRestore();
    }

    private void SuspendSegmented()
    {
        CancelSegmentedRestore();
        if (QuickCaptureViewSegmented is not null)
        {
            QuickCaptureViewSegmented.Visibility = Visibility.Collapsed;
        }
    }

    private void PrepareSegmentedForExpansion(double targetContentWidth)
    {
        if (QuickCaptureViewSegmented is null ||
            ViewModel.TabBarVisibility != Visibility.Visible ||
            ListPage.Visibility != Visibility.Visible ||
            !double.IsFinite(targetContentWidth) ||
            targetContentWidth < WidgetSegmentedLayoutHelper.MinimumSafeWidth)
        {
            return;
        }

        // WidgetShell freezes the content presenter at the final expanded
        // width before starting the HWND animation. Realize the Segmented tree
        // against that safe slot now, so the tabs are already present when the
        // compact layer begins to fade instead of appearing after three more
        // stable frames. The initial-load fallback below still protects any
        // genuinely zero-width layout pass.
        CancelSegmentedRestore();
        RevealSegmentedAtSelectedView();
    }

    private void SynchronizeSegmentedVisibility()
    {
        if (_isDisposed || QuickCaptureViewSegmented is null)
        {
            return;
        }

        if (ViewModel.TabBarVisibility != Visibility.Visible ||
            ListPage.Visibility != Visibility.Visible)
        {
            SuspendSegmented();
            return;
        }

        QueueSegmentedRestore();
    }

    private void QueueSegmentedRestore()
    {
        if (!IsLoaded ||
            _isResponsiveLayoutTransitionActive ||
            QuickCaptureViewSegmented is null ||
            ViewModel.TabBarVisibility != Visibility.Visible ||
            ListPage.Visibility != Visibility.Visible ||
            QuickCaptureViewSegmented.Visibility == Visibility.Visible ||
            _segmentedRestoreHandler is not null)
        {
            return;
        }

        _segmentedStableFrames = 0;
        _segmentedCandidateWidth = 0;
        _segmentedRestoreHandler = SegmentedRestore_Rendering;
        CompositionTarget.Rendering += _segmentedRestoreHandler;
    }

    private void SegmentedRestore_Rendering(object? sender, object e)
    {
        if (ViewModel.TabBarVisibility != Visibility.Visible ||
            ListPage.Visibility != Visibility.Visible)
        {
            SuspendSegmented();
            return;
        }

        if (_isResponsiveLayoutTransitionActive)
        {
            _segmentedStableFrames = 0;
            return;
        }

        double width = Math.Min(ListPage.ActualWidth, ResponsiveContentGrid.ActualWidth);
        if (!double.IsFinite(width) ||
            width < WidgetSegmentedLayoutHelper.MinimumSafeWidth)
        {
            _segmentedStableFrames = 0;
            _segmentedCandidateWidth = width;
            return;
        }

        if (Math.Abs(width - _segmentedCandidateWidth) > 0.5)
        {
            _segmentedCandidateWidth = width;
            _segmentedStableFrames = 1;
            return;
        }

        if (++_segmentedStableFrames < 3)
        {
            return;
        }

        CancelSegmentedRestore();
        RevealSegmentedAtSelectedView();
    }

    private void RevealSegmentedAtSelectedView()
    {
        if (QuickCaptureViewSegmented is null)
        {
            return;
        }

        // Loading or revealing the toolkit control can emit SelectionChanged
        // for its template default. Keep those events programmatic, apply the
        // restored member state on both sides of realization, and only then
        // allow user-driven selection changes.
        bool wasSynchronizing = _isSynchronizingViewSelection;
        _isSynchronizingViewSelection = true;
        try
        {
            ApplySegmentedStyle();
            UpdateSelectedViewVisual();
            QuickCaptureViewSegmented.Visibility = Visibility.Visible;
            UpdateSelectedViewVisual();
        }
        finally
        {
            _isSynchronizingViewSelection = wasSynchronizing;
        }
    }

    private void CancelSegmentedRestore()
    {
        if (_segmentedRestoreHandler is not null)
        {
            CompositionTarget.Rendering -= _segmentedRestoreHandler;
            _segmentedRestoreHandler = null;
        }
        _segmentedStableFrames = 0;
        _segmentedCandidateWidth = 0;
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(AddInputWithFeedbackAsync);
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private async void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool controlPressed = Win32Helper.IsKeyPressed(
            Windows.System.VirtualKey.Control);
        bool saveShortcut = TextBoxEditorShortcutHelper.IsCtrlSaveShortcut(
            e.Key,
            controlPressed,
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift));
        if (e.Key != Windows.System.VirtualKey.Enter && !saveShortcut)
        {
            return;
        }

        e.Handled = true;
        if (saveShortcut || SettingsService.ShouldSubmitEditorOnEnter(
                _settingsService.Settings.QuickCaptureEditorEnterBehavior,
                controlPressed))
        {
            await RunAsync(AddInputWithFeedbackAsync);
            return;
        }

        TextBoxEditorShortcutHelper.InsertLineBreak(InputTextBox);
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseSearchAndRestoreFocus();
            e.Handled = true;
        }
    }

    private void CloseSearchButton_Click(object sender, RoutedEventArgs e)
    {
        CloseSearchAndRestoreFocus();
    }

    private void CloseSearchAndRestoreFocus()
    {
        ViewModel.CollapseSearch();
        DispatcherQueue.TryEnqueue(() =>
            SearchButton.Focus(FocusState.Programmatic));
    }

    private async void ViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !Enum.TryParse(tag, ignoreCase: true, out QuickCaptureViewMode mode))
        {
            return;
        }

        await SwitchViewAsync(mode);
    }

    private async void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not QuickCaptureItemViewModel item)
        {
            return;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            return;
        }

        await OpenDetailAfterSavingAsync(item);
        await Task.CompletedTask;
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExpandSearch();
        DispatcherQueue.TryEnqueue(() => SearchTextBox.Focus(FocusState.Programmatic));
    }

    private async void AddNoteCardButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenNewDetailAsync();
    }

    private async Task OpenNewDetailAsync()
    {
        await FlushPendingDetailSaveAsync();
        if (_detailHasUnsavedChanges)
        {
            return;
        }

        _detailAutoSaveTimer?.Stop();
        _isCreatingDetail = true;
        _detailItem = null;
        _detailAppearance = QuickCaptureAppearancePreset.Default;
        _detailContentFormat = ViewModel.EditorContentFormat;
        _pendingDetailAttachments.Clear();
        _isDetailEditing = true;
        _detailEditRevision = 0;
        _detailSavedRevision = 0;
        _detailHasUnsavedChanges = false;
        _showDetailInSinglePane = !_isDualPane;
        SetDetailEditorText(string.Empty);
        DetailMarkdownEditor.ShowFormattingToolbar =
            _detailContentFormat == TextContentFormat.Markdown;
        DetailTimestampText.Text = _localizationService.Format(
            "QuickCapture.Detail.Created",
            DateTimeOffset.Now.ToString("yyyy/M/d HH:mm"));
        RefreshDetailAttachments();
        ApplyDetailMaterialSurface();
        ApplyResponsiveLayout();
        RefreshDetailPresentation();
        DispatcherQueue.TryEnqueue(() =>
            DetailMarkdownEditor.FocusEditor(moveCaretToEnd: false));
    }

    private void OpenDetail(QuickCaptureItemViewModel item)
    {
        _detailAutoSaveTimer?.Stop();
        _isCreatingDetail = false;
        _detailItem = item;
        _detailAppearance = item.AppearancePreset;
        _pendingDetailAttachments.Clear();
        _isDetailEditing = !item.IsRecent &&
            (!_isDualPane || SettingsService.NormalizeQuickCaptureWideOpenMode(
                _settingsService.Settings.QuickCaptureWideOpenMode) ==
                SettingsService.QuickCaptureWideOpenEditing);
        _detailContentFormat = _isDetailEditing
            ? ViewModel.EditorContentFormat
            : item.ContentFormat;
        _detailEditRevision = 0;
        _detailSavedRevision = 0;
        _detailHasUnsavedChanges = false;
        _showDetailInSinglePane = !_isDualPane;
        SetDetailEditorText(_isDetailEditing ? item.Body : string.Empty);
        DetailMarkdownEditor.ShowFormattingToolbar =
            _detailContentFormat == TextContentFormat.Markdown;
        DetailMarkdownView.Markdown = item.Body;
        DetailMarkdownView.ContentFormat = _detailContentFormat;
        DetailMarkdownView.AllowRemoteImages =
            _settingsService.Settings.QuickCaptureAllowRemoteImages;
        DetailMarkdownView.AreTaskListsInteractive =
            !item.IsRecent && _detailContentFormat == TextContentFormat.Markdown;
        DetailTimestampText.Text = _localizationService.Format(
            "QuickCapture.Detail.Created",
            item.ToModel().CreatedAt.ToLocalTime().ToString("yyyy/M/d HH:mm"));
        DetailPinIcon.IsPinned = item.IsPinned;
        RefreshDetailAttachments();
        ApplyDetailMaterialSurface();
        foreach (QuickCaptureItemViewModel candidate in ViewModel.Items)
        {
            candidate.IsDetailSelected = candidate.Id == item.Id;
        }
        ApplyResponsiveLayout();
        RefreshDetailPresentation();
    }

    private async Task OpenDetailAfterSavingAsync(QuickCaptureItemViewModel item)
    {
        if (_detailItem is not null &&
            string.Equals(_detailItem.Id, item.Id, StringComparison.Ordinal))
        {
            if (!_isDualPane)
            {
                _showDetailInSinglePane = true;
                ApplyResponsiveLayout();
            }
            return;
        }

        await FlushPendingDetailSaveAsync();
        if (_detailHasUnsavedChanges)
        {
            return;
        }

        OpenDetail(item);
    }

    private void RefreshDetailPresentation()
    {
        bool hasDetail = _isCreatingDetail || _detailItem is not null;
        bool isReadOnly = _detailItem?.IsRecent == true;
        DetailEmptyState.Visibility = _isDualPane &&
                                      !hasDetail &&
                                      !ViewModel.IsSwitchingView
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailHeader.Visibility = hasDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailContent.Visibility = hasDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailEditButton.Visibility = hasDetail && !_isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailDoneButton.Visibility = hasDetail &&
                                      _isDetailEditing &&
                                      !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_isDualPane)
        {
            DetailBackColumn.Width = new GridLength(8);
        }
        DetailPinButton.Visibility = _detailItem is { IsRecent: false }
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailCopyButton.Visibility = _detailItem is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailDeleteButton.Visibility = _detailItem is not null && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailAddFileButton.Visibility = hasDetail && _isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailAttachmentStrip.CanRemove = hasDetail && _isDetailEditing && !isReadOnly;
        bool hasPrimaryImage = !string.IsNullOrWhiteSpace(_detailPrimaryImagePath);
        bool showDetailText = _isDetailEditing ||
                              !hasPrimaryImage ||
                              HasMeaningfulDetailText();
        ApplyDetailPrimaryImageLayout(hasPrimaryImage, showDetailText);
        DetailMarkdownEditor.Visibility = hasDetail &&
                                          _isDetailEditing &&
                                          !isReadOnly &&
                                          showDetailText
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailMarkdownView.Visibility = hasDetail &&
                                        (!_isDetailEditing || isReadOnly) &&
                                        !_deferDetailReaderUntilTransitionCompletes &&
                                        showDetailText
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailMaterialPalette.Visibility = hasDetail && _isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (hasDetail)
        {
            DetailMarkdownView.Markdown = GetDetailPresentationBody();
            DetailMarkdownView.ContentFormat = _detailContentFormat;
            DetailMarkdownView.AllowRemoteImages =
                _settingsService.Settings.QuickCaptureAllowRemoteImages;
            DetailMarkdownView.AreTaskListsInteractive =
                !isReadOnly && _detailContentFormat == TextContentFormat.Markdown;
            DetailMarkdownEditor.ShowFormattingToolbar =
                _detailContentFormat == TextContentFormat.Markdown;
        }
    }

    private void DetailEditButton_Click(object sender, RoutedEventArgs e)
    {
        BeginDetailEditing();
    }

    private void DetailBodyReaderSurface_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        if (BeginDetailEditing())
        {
            e.Handled = true;
        }
    }

    private bool BeginDetailEditing()
    {
        if (_isDetailEditing ||
            _detailItem?.IsRecent == true ||
            (!_isCreatingDetail && _detailItem is null))
        {
            return false;
        }

        _detailContentFormat = ViewModel.EditorContentFormat;
        SetDetailEditorText(_detailItem?.Body ?? string.Empty);
        _isDetailEditing = true;
        RefreshDetailPresentation();
        DispatcherQueue.TryEnqueue(() =>
            DetailMarkdownEditor.FocusEditor(moveCaretToEnd: false));
        return true;
    }

    private async void DetailDoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveDetailAsync(completeEditing: true))
        {
            RaiseFeedback(
                T("QuickCapture.Saved"),
                WidgetFeedbackSeverity.Success,
                "quick-detail-saved");
        }
    }

    private async void DetailMarkdownEditor_CommitRequested(object? sender, EventArgs e)
    {
        if (await SaveDetailAsync(completeEditing: true))
        {
            RaiseFeedback(
                T("QuickCapture.Saved"),
                WidgetFeedbackSeverity.Success,
                "quick-detail-saved");
        }
    }

    private void DetailMarkdownEditor_EditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressDetailEditorChanges ||
            !_isDetailEditing ||
            _detailItem?.IsRecent == true)
        {
            return;
        }

        MarkDetailDirty();
        ScheduleDetailAutoSave();
    }

    private void MarkDetailDirty()
    {
        _detailEditRevision++;
        _detailHasUnsavedChanges = _detailEditRevision != _detailSavedRevision;
    }

    private void ScheduleDetailAutoSave()
    {
        _detailAutoSaveTimer?.Stop();
        if (!_isCreatingDetail)
        {
            _detailAutoSaveTimer?.Start();
        }
    }

    private async void DetailAutoSaveTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (!_isCreatingDetail && _detailHasUnsavedChanges)
        {
            await SaveDetailAsync(completeEditing: false);
        }
    }

    private async Task FlushPendingDetailSaveAsync()
    {
        _detailAutoSaveTimer?.Stop();
        if (_isCreatingDetail && !HasNewDetailContent())
        {
            // A blank draft has nothing to preserve. Clearing the dirty flag
            // lets a list click leave the new-note surface immediately.
            _detailEditRevision = _detailSavedRevision;
            _detailHasUnsavedChanges = false;
            return;
        }

        if (_detailHasUnsavedChanges || _isSavingDetail)
        {
            await SaveDetailAsync(completeEditing: false);
        }
    }

    private bool HasNewDetailContent() =>
        !string.IsNullOrWhiteSpace(DetailMarkdownEditor.Text) ||
        _pendingDetailAttachments.Count > 0;

    private async Task<bool> SaveDetailAsync(bool completeEditing)
    {
        _detailAutoSaveTimer?.Stop();
        await _detailSaveGate.WaitAsync();
        try
        {
            _isSavingDetail = true;
            bool saved;
            do
            {
                saved = await SaveDetailCoreAsync();
                if (!saved)
                {
                    return false;
                }
            }
            while (completeEditing && _detailHasUnsavedChanges);

            if (completeEditing)
            {
                _isDetailEditing = false;
            }

            RefreshDetailAfterSave();
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Detail save failed id={WidgetId}: {ex}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-detail-save-error");
            return false;
        }
        finally
        {
            _isSavingDetail = false;
            _detailSaveGate.Release();
            if (_detailHasUnsavedChanges && _isDetailEditing && !_isCreatingDetail &&
                !string.IsNullOrWhiteSpace(DetailMarkdownEditor.Text))
            {
                // An emptied body fails validation on every attempt; re-arming
                // the auto-save here would toast "content cannot be empty"
                // forever. Typing re-arms the timer via ScheduleDetailAutoSave.
                _detailAutoSaveTimer?.Start();
            }
        }
    }

    private async Task<bool> SaveDetailCoreAsync()
    {
        long revisionAtStart = _detailEditRevision;
        string body = DetailMarkdownEditor.Text;
        if (_detailItem?.IsRecent == true)
        {
            _detailSavedRevision = revisionAtStart;
            _detailHasUnsavedChanges = false;
            return true;
        }

        if (_isCreatingDetail)
        {
            if (string.IsNullOrWhiteSpace(body) && _pendingDetailAttachments.Count == 0)
            {
                RaiseFeedback(
                    T("QuickCapture.EmptyEdit"),
                    WidgetFeedbackSeverity.Warning,
                    "quick-detail-empty");
                return false;
            }

            QuickCaptureItem? created = null;
            if (_pendingDetailAttachments.Count > 0)
            {
                QuickCaptureItemViewModel? attached =
                    await ViewModel.AddItemWithAttachmentsAsync(_pendingDetailAttachments);
                if (attached is null)
                {
                    return false;
                }

                QuickCaptureWriteResult updateResult =
                    await ViewModel.EditItemDetailsWithResultAsync(
                        attached,
                        null,
                        body,
                        _detailAppearance,
                        _detailContentFormat);
                if (!updateResult.Saved)
                {
                    return false;
                }

                ReportBodyTruncation(updateResult);
                created = updateResult.Item ?? attached.ToModel();
            }
            else if (!string.IsNullOrWhiteSpace(body))
            {
                QuickCaptureWriteResult addResult =
                    await ViewModel.AddDetailedItemWithResultAsync(
                    null,
                    body,
                    _detailAppearance,
                    _detailContentFormat);
                ReportBodyTruncation(addResult);
                created = addResult.Item;
            }

            if (created is not null)
            {
                await ViewModel.RefreshItemsAsync();
                _detailItem = ViewModel.Items.FirstOrDefault(item => item.Id == created.Id);
                _isCreatingDetail = false;
                _pendingDetailAttachments.Clear();
            }

            _detailSavedRevision = Math.Max(_detailSavedRevision, revisionAtStart);
            _detailHasUnsavedChanges = _detailEditRevision > revisionAtStart;
            return true;
        }

        if (_detailItem is not { IsRecent: false } item)
        {
            return !_detailHasUnsavedChanges;
        }

        bool detailsChanged =
            _detailHasUnsavedChanges ||
            _detailAppearance != item.AppearancePreset ||
            _detailContentFormat != item.ContentFormat;
        if (detailsChanged)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                RaiseFeedback(
                    T("QuickCapture.EmptyEdit"),
                    WidgetFeedbackSeverity.Warning,
                    "quick-detail-empty");
                return false;
            }

            QuickCaptureWriteResult updateResult =
                await ViewModel.EditItemDetailsWithResultAsync(
                    item,
                    null,
                    body,
                    _detailAppearance,
                    _detailContentFormat);
            if (!updateResult.Saved)
            {
                return false;
            }

            ReportBodyTruncation(updateResult);
            await ViewModel.RefreshItemsAsync();
            _detailItem = ViewModel.Items.FirstOrDefault(entry => entry.Id == item.Id);
        }

        _detailSavedRevision = Math.Max(_detailSavedRevision, revisionAtStart);
        _detailHasUnsavedChanges = _detailEditRevision > revisionAtStart;
        return true;
    }

    private void RefreshDetailAfterSave()
    {
        if (_detailItem is { } refreshed)
        {
            if (!_detailHasUnsavedChanges &&
                !string.Equals(DetailMarkdownEditor.Text, refreshed.Body, StringComparison.Ordinal))
            {
                SetDetailEditorText(refreshed.Body);
            }

            DetailMarkdownView.Markdown = _isDetailEditing
                ? DetailMarkdownEditor.Text
                : refreshed.Body;
            DetailMarkdownView.ContentFormat = _detailContentFormat;
            DetailPinIcon.IsPinned = refreshed.IsPinned;
            foreach (QuickCaptureItemViewModel candidate in ViewModel.Items)
            {
                candidate.IsDetailSelected = candidate.Id == refreshed.Id;
            }
            RefreshDetailAttachments();
            ApplyDetailMaterialSurface();
            RefreshDetailPresentation();
            if (!_isDetailEditing)
            {
                SetDetailEditorText(string.Empty);
            }
        }
        else if (!_isCreatingDetail)
        {
            _showDetailInSinglePane = false;
            ApplyResponsiveLayout();
            RefreshDetailPresentation();
        }
    }

    private void SetDetailEditorText(string value)
    {
        _suppressDetailEditorChanges = true;
        try
        {
            DetailMarkdownEditor.Text = value ?? string.Empty;
        }
        finally
        {
            _suppressDetailEditorChanges = false;
        }
    }

    private async void DetailBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCreatingDetail)
        {
            ClearDetailForViewChange();
            return;
        }

        await FlushPendingDetailSaveAsync();
        if (_detailHasUnsavedChanges)
        {
            return;
        }
        _isCreatingDetail = false;
        _isDetailEditing = false;
        _showDetailInSinglePane = false;
        ApplyResponsiveLayout();
        RefreshDetailPresentation();
    }

    private async void DetailPinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is { IsRecent: false } item)
        {
            await FlushPendingDetailSaveAsync();
            if (!await ToggleItemPinnedWithFeedbackAsync(item))
            {
                return;
            }

            await ViewModel.RefreshItemsAsync();
            QuickCaptureItemViewModel? refreshed =
                ViewModel.Items.FirstOrDefault(entry => entry.Id == item.Id);
            if (refreshed is not null)
            {
                _detailItem = refreshed;
                DetailPinIcon.IsPinned = refreshed.IsPinned;
                foreach (QuickCaptureItemViewModel candidate in ViewModel.Items)
                {
                    candidate.IsDetailSelected = candidate.Id == refreshed.Id;
                }
                RefreshDetailPresentation();
            }
        }
    }

    private async void DetailCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is { } item)
        {
            await CopyItemWithFeedbackAsync(item);
        }
    }

    private void DetailMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse(tag, out QuickCaptureAppearancePreset preset))
        {
            _detailAppearance = preset;
            ApplyDetailMaterialSurface();
            MarkDetailDirty();
            RefreshItemMaterialSurfaces();
            ScheduleDetailAutoSave();
        }
    }

    private void ApplyDetailMaterialSurface()
    {
        if (DetailMaterialSurface is null)
        {
            return;
        }

        DetailMaterialSurface.Background = ResolveMaterialBrush(_detailAppearance);
        foreach (Button button in GetDetailMaterialButtons())
        {
            bool selected = string.Equals(
                button.Tag as string,
                _detailAppearance.ToString(),
                StringComparison.Ordinal);
            button.BorderBrush = selected
                ? new SolidColorBrush(
                    App.Current.ThemeService?.GetEffectiveAccentColor() ??
                    AccentColorHelper.DefaultAccentColor)
                : new SolidColorBrush(Colors.Transparent);
            button.BorderThickness = new Thickness(selected ? 1.5 : 1);
        }
    }

    private IEnumerable<Button> GetDetailMaterialButtons()
    {
        yield return DefaultMaterialButton;
        yield return PaperMaterialButton;
        yield return YellowMaterialButton;
        yield return RoseMaterialButton;
        yield return MintMaterialButton;
        yield return BlueMaterialButton;
    }

    private async void DetailDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not { IsRecent: false } item)
        {
            return;
        }

        await DeleteQuickCaptureItemAsync(item);
    }

    private async void DetailMarkdownView_TaskToggleRequested(
        object? sender,
        MarkdownTaskToggleRequestedEventArgs e)
    {
        if (_detailItem?.IsRecent == true ||
            _detailContentFormat != TextContentFormat.Markdown ||
            !_markdownDocumentService.TryToggleTask(
                GetDetailPresentationBody(),
                e.TaskIndex,
                out string updated))
        {
            return;
        }

        SetDetailEditorText(updated);
        MarkDetailDirty();
        DetailMarkdownView.Markdown = updated;
        await SaveDetailAsync(completeEditing: false);
    }

    private async void DetailAddFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDetailEditing || _detailItem?.IsRecent == true)
        {
            return;
        }

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add("*");
            IntPtr foreground = Win32Helper.GetForegroundWindow();
            IntPtr owner = Win32Helper.GetAncestor(foreground, Win32Helper.GA_ROOT);
            InitializeWithWindow.Initialize(
                picker,
                owner == IntPtr.Zero ? foreground : owner);
            IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
            DroppedFilePath[] paths = files
                .Where(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
                .Select(file => new DroppedFilePath(file.Path, file.Name, ForceManagedCopy: false))
                .ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            if (_isCreatingDetail || _detailItem is null)
            {
                foreach (DroppedFilePath path in paths)
                {
                    if (!_pendingDetailAttachments.Any(existing =>
                            string.Equals(existing.Path, path.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        _pendingDetailAttachments.Add(path);
                    }
                }
                MarkDetailDirty();
            }
            else
            {
                _detailItem = await ViewModel.AddAttachmentsAsync(_detailItem, paths) ??
                    _detailItem;
            }

            RefreshDetailAttachments();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Add attachment failed: {ex}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-attachment-error");
        }
    }

    private async void DetailAttachmentStrip_OpenRequested(
        object? sender,
        AttachmentTileEventArgs e)
    {
        TodoAttachmentViewModel attachment = e.Attachment;
        if (!File.Exists(attachment.FilePath))
        {
            return;
        }

        StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
        await Windows.System.Launcher.LaunchFileAsync(file);
    }

    private async void DetailAttachmentStrip_RemoveRequested(
        object? sender,
        AttachmentTileEventArgs e)
    {
        if (!_isDetailEditing || _detailItem?.IsRecent == true)
        {
            return;
        }

        TodoAttachmentViewModel attachment = e.Attachment;

        if (_isCreatingDetail || _detailItem is null)
        {
            int removed = _pendingDetailAttachments.RemoveAll(file =>
                string.Equals(file.Path, attachment.FilePath, StringComparison.OrdinalIgnoreCase));
            RefreshDetailAttachments();
            if (removed > 0)
            {
                MarkDetailDirty();
            }
            return;
        }

        if (_detailItem.Attachments.Count == 1 &&
            string.IsNullOrWhiteSpace(DetailMarkdownEditor.Text))
        {
            RaiseFeedback(
                T("QuickCapture.EmptyEdit"),
                WidgetFeedbackSeverity.Warning,
                "quick-detail-empty");
            return;
        }

        QuickCaptureItemViewModel? updated = await ViewModel.DeleteAttachmentAsync(
            _detailItem,
            attachment.Id);
        if (updated is not null)
        {
            _detailItem = updated;
            RefreshDetailAttachments();
            ApplyDetailMaterialSurface();
        }
    }

    private string GetDetailPresentationBody()
    {
        if (_isCreatingDetail || _isDetailEditing)
        {
            return DetailMarkdownEditor.Text;
        }

        return _detailItem?.Body ?? string.Empty;
    }

    private bool HasMeaningfulDetailText()
    {
        string body = GetDetailPresentationBody();
        return !string.IsNullOrWhiteSpace(body) &&
               !(_detailItem?.Type == QuickCaptureItemType.Image &&
                 string.Equals(body.Trim(), "Image", StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyDetailPrimaryImageLayout(bool hasPrimaryImage, bool showDetailText)
    {
        DetailPrimaryImageHost.Visibility = hasPrimaryImage
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailPrimaryImageRow.Height = hasPrimaryImage
            ? new GridLength(showDetailText ? 2 : 1, GridUnitType.Star)
            : new GridLength(0);
        DetailBodyTextRow.Height = showDetailText
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
    }

    private void RefreshDetailAttachments()
    {
        IReadOnlyList<TodoAttachmentViewModel> attachments =
            _detailItem?.Attachments ??
            _pendingDetailAttachments
                .Select(file => new TodoAttachmentViewModel(new TodoAttachment
                {
                    FilePath = file.Path,
                    DisplayName = file.DisplayName,
                    Type = AttachmentStorageService.GetAttachmentType(file.Path)
                }))
                .ToArray();
        string attachmentRenderKey = CreateDetailAttachmentRenderKey();
        if (!string.Equals(
                _detailAttachmentRenderKey,
                attachmentRenderKey,
                StringComparison.Ordinal))
        {
            _detailAttachmentRenderKey = attachmentRenderKey;
            DetailMarkdownView.Refresh();
        }

        // ItemsSource crosses the WinRT object-valued dependency-property ABI.
        // Project the typed read-only list to a concrete object array so the
        // empty and populated attachment states both remain Native AOT safe.
        DetailAttachmentStrip.ItemsSource = attachments.Cast<object>().ToArray();
        DetailAttachmentStrip.CanRemove = _isDetailEditing && _detailItem?.IsRecent != true;
        DetailAttachmentStrip.Visibility = attachments.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        bool showPrimaryImage = _detailItem?.Type == QuickCaptureItemType.Image &&
                                _detailItem.SourceKind == QuickCaptureSourceKind.Clipboard;
        string? primaryImagePath = showPrimaryImage &&
                                   !string.IsNullOrWhiteSpace(_detailItem?.ImagePath)
            ? _detailItem!.ImagePath
            : null;
        _ = RefreshDetailPrimaryImageAsync(primaryImagePath);
    }

    private string CreateDetailAttachmentRenderKey()
    {
        if (_detailItem is { } item)
        {
            return item.Id + "\u001D" + string.Join(
                "\u001F",
                item.Attachments.Select(attachment =>
                    attachment.Id + "\u001E" + attachment.FilePath));
        }

        return _isCreatingDetail
            ? "new\u001D" + string.Join(
                "\u001F",
                _pendingDetailAttachments.Select(attachment => attachment.Path))
            : string.Empty;
    }

    private async Task RefreshDetailPrimaryImageAsync(string? imagePath)
    {
        long loadVersion = ++_detailImageLoadVersion;
        _detailPrimaryImagePath = string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)
            ? null
            : imagePath;
        DetailPrimaryImage.Source = null;

        bool hasPrimaryImage = _detailPrimaryImagePath is not null;
        bool showDetailText = _isDetailEditing ||
                              !hasPrimaryImage ||
                              HasMeaningfulDetailText();
        ApplyDetailPrimaryImageLayout(hasPrimaryImage, showDetailText);
        DetailPrimaryImageLoadingRing.IsActive = hasPrimaryImage;
        DetailPrimaryImageLoadingRing.Visibility = hasPrimaryImage
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!hasPrimaryImage)
        {
            return;
        }

        string primaryImagePath = _detailPrimaryImagePath!;
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(primaryImagePath);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage
            {
                DecodePixelWidth = DetailImageDecodePixelWidth
            };
            await bitmap.SetSourceAsync(stream);
            if (_isDisposed || loadVersion != _detailImageLoadVersion)
            {
                return;
            }

            DetailPrimaryImage.Source = bitmap;
            DetailPrimaryImageLoadingRing.IsActive = false;
            DetailPrimaryImageLoadingRing.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (_isDisposed || loadVersion != _detailImageLoadVersion)
            {
                return;
            }

            App.Log($"[QuickCaptureSurface] Detail image preview failed: {ex}");
            _detailPrimaryImagePath = null;
            DetailPrimaryImageLoadingRing.IsActive = false;
            DetailPrimaryImageLoadingRing.Visibility = Visibility.Collapsed;
            ApplyDetailPrimaryImageLayout(hasPrimaryImage: false, showDetailText: true);
            RefreshDetailPresentation();
        }
    }

    private string? ResolveDetailAttachmentPath(string attachmentId) =>
        _detailItem?.Attachments.FirstOrDefault(attachment =>
            string.Equals(attachment.Id, attachmentId, StringComparison.Ordinal))?.FilePath;

    private async void DetailMarkdownView_AttachmentOpenRequested(
        object? sender,
        MarkdownAttachmentRequestedEventArgs e)
    {
        string? path = ResolveDetailAttachmentPath(e.AttachmentId);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            await Windows.System.Launcher.LaunchFileAsync(file);
        }
    }

    private async void PinItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickCaptureItemViewModel item })
        {
            await ToggleItemPinnedWithFeedbackAsync(item);
        }
    }

    private async Task<bool> ToggleItemPinnedWithFeedbackAsync(
        QuickCaptureItemViewModel item)
    {
        bool willPin = item.IsRecent || !item.IsPinned;
        bool changed = false;
        await RunAsync(async () =>
        {
            changed = item.IsRecent
                ? await ViewModel.PinRecentItemAsync(item)
                : await ViewModel.TogglePinnedAsync(item);
        });
        if (changed)
        {
            RaiseFeedback(
                T(willPin
                    ? "QuickCapture.PinnedSuccess"
                    : "QuickCapture.UnpinnedSuccess"),
                WidgetFeedbackSeverity.Success,
                willPin ? "quick-pinned" : "quick-unpinned");
        }

        return changed;
    }

    private async void CopyItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickCaptureItemViewModel item })
        {
            await CopyItemWithFeedbackAsync(item);
        }
    }

    private async Task<bool> CopyItemWithFeedbackAsync(
        QuickCaptureItemViewModel item)
    {
        bool copied = false;
        await RunAsync(async () =>
        {
            await ViewModel.CopyItemAsync(item);
            copied = true;
        });
        if (copied)
        {
            RaiseFeedback(
                T("QuickCapture.Copied"),
                WidgetFeedbackSeverity.Success,
                "quick-copied");
        }

        return copied;
    }

    private void QuickCaptureItem_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (sender is not Border
            {
                DataContext: QuickCaptureItemViewModel item
            } anchor)
        {
            return;
        }

        ItemsList.SelectedItem = item;
        MenuFlyout flyout = CreateItemContextFlyout(item);
        flyout.ShowAt(
            anchor,
            new FlyoutShowOptions
            {
                Position = e.GetPosition(anchor),
                ShowMode = FlyoutShowMode.Standard
            });
        e.Handled = true;
    }

    private MenuFlyout CreateItemContextFlyout(QuickCaptureItemViewModel item)
    {
        var flyout = new MenuFlyout();

        if (!item.IsRecent)
        {
            var editItem = CreateContextMenuItem("QuickCapture.Edit", "\uE70F");
            editItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await OpenDetailAfterSavingAsync(item);
                if (_detailItem is not null)
                {
                    BeginDetailEditing();
                }
            };
            flyout.Items.Add(editItem);

            var pinItem = new MenuFlyoutItem
            {
                Text = T(item.IsPinned ? "QuickCapture.Unpin" : "QuickCapture.Pin"),
                Icon = new FontIcon { Glyph = "\uE718" }
            };
            pinItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await ToggleItemPinnedWithFeedbackAsync(item);
            };
            flyout.Items.Add(pinItem);
        }

        var copyItem = CreateContextMenuItem("Common.Copy", "\uE8C8");
        copyItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await CopyItemWithFeedbackAsync(item);
        };
        flyout.Items.Add(copyItem);

        if (item.IsRecent)
        {
            var saveItem = CreateContextMenuItem("QuickCapture.SaveToRecords", "\uE74E");
            saveItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await ViewModel.SaveRecentItemAsync(item);
            };
            flyout.Items.Add(saveItem);
        }
        else
        {
            flyout.Items.Add(CreateAppearanceContextSubmenu(item, flyout));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var deleteItem = CreateContextMenuItem("Common.Delete", "\uE74D");
        deleteItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await DeleteQuickCaptureItemAsync(item);
        };
        flyout.Items.Add(deleteItem);
        return flyout;
    }

    private MenuFlyoutSubItem CreateAppearanceContextSubmenu(
        QuickCaptureItemViewModel item,
        MenuFlyout owner)
    {
        var submenu = new MenuFlyoutSubItem
        {
            Text = T("QuickCapture.Detail.Appearance"),
            Icon = new FontIcon { Glyph = "\uE790" }
        };
        foreach ((QuickCaptureAppearancePreset preset, string textKey) in new[]
        {
            (QuickCaptureAppearancePreset.Default, "QuickCapture.Material.Default"),
            (QuickCaptureAppearancePreset.Paper, "QuickCapture.Material.Paper"),
            (QuickCaptureAppearancePreset.StickyYellow, "QuickCapture.Material.Yellow"),
            (QuickCaptureAppearancePreset.Rose, "QuickCapture.Material.Rose"),
            (QuickCaptureAppearancePreset.Mint, "QuickCapture.Material.Mint"),
            (QuickCaptureAppearancePreset.MistBlue, "QuickCapture.Material.Blue")
        })
        {
            var appearanceItem = new ToggleMenuFlyoutItem
            {
                Text = T(textKey),
                IsChecked = item.AppearancePreset == preset
            };
            appearanceItem.Click += async (_, _) =>
            {
                owner.Hide();
                if (!await ViewModel.SetAppearanceAsync(item, preset))
                {
                    return;
                }

                await ViewModel.RefreshItemsAsync();
                QuickCaptureItemViewModel? refreshed = ViewModel.Items.FirstOrDefault(
                    candidate => candidate.Id == item.Id);
                if (_detailItem?.Id == item.Id && refreshed is not null)
                {
                    _detailItem = refreshed;
                    _detailAppearance = preset;
                    ApplyDetailMaterialSurface();
                }
            };
            submenu.Items.Add(appearanceItem);
        }

        return submenu;
    }

    private MenuFlyoutItem CreateContextMenuItem(string textKey, string glyph) =>
        new()
        {
            Text = T(textKey),
            Icon = new FontIcon { Glyph = glyph }
        };

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuickCaptureItemViewModel item })
        {
            return;
        }

        await DeleteQuickCaptureItemAsync(item);
    }

    private async Task DeleteQuickCaptureItemAsync(QuickCaptureItemViewModel item)
    {
        QuickCaptureDeletedItemSnapshot? snapshot = null;
        await RunAsync(async () => snapshot = await ViewModel.DeleteItemAsync(item));
        if (snapshot is null)
        {
            return;
        }

        bool deletedOpenDetail = _detailItem?.Id == item.Id;
        if (deletedOpenDetail)
        {
            _detailItem = null;
            _isDetailEditing = false;
            _detailHasUnsavedChanges = false;
            _showDetailInSinglePane = false;
            ApplyResponsiveLayout();
            ReconcileDetailSelection();
        }

        QuickCaptureDeletedItemSnapshot deletedSnapshot = snapshot;
        RaiseFeedback(
            T("QuickCapture.Deleted"),
            WidgetFeedbackSeverity.Success,
            "quick-delete",
            T("Common.Undo"),
            async () =>
            {
                if (await ViewModel.RestoreDeletedItemAsync(deletedSnapshot))
                {
                    await ViewModel.RefreshItemsAsync();
                    ReconcileDetailSelection();
                }
            });
    }

    private void QuickCaptureItem_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: QuickCaptureItemViewModel item
            })
        {
            ItemsList.CanReorderItems = false;
            _pendingPointerDragItems = [];
            return;
        }

        bool isLeftButtonPressed =
            e.GetCurrentPoint(ItemsList).Properties.IsLeftButtonPressed;
        bool itemIsSelected = ItemsList.SelectedItems.Contains(item);
        ItemsList.CanReorderItems = false;

        if (!isLeftButtonPressed || !itemIsSelected)
        {
            _pendingPointerDragItems = [];
            return;
        }

        _pendingPointerDragItems = ItemsList.SelectedItems
            .OfType<QuickCaptureItemViewModel>()
            .ToArray();
    }

    private void QuickCaptureItem_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        _pendingPointerDragItems = [];
        if (!_isInternalQuickCaptureDrag)
        {
            ItemsList.CanReorderItems = false;
        }
    }

    private void ItemsList_DragItemsStarting(
        object sender,
        DragItemsStartingEventArgs e)
    {
        QuickCaptureItemViewModel[] eventItems = e.Items
            .OfType<QuickCaptureItemViewModel>()
            .ToArray();
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems =
            _pendingPointerDragItems.Length > 1
                ? _pendingPointerDragItems
                : ItemsList.SelectedItems
                    .OfType<QuickCaptureItemViewModel>()
                    .ToArray();
        _pendingPointerDragItems = [];
        IReadOnlyList<QuickCaptureItemViewModel> draggedItems =
            QuickCaptureDragPackage.ResolveDraggedItems(
                eventItems,
                selectedItems);
        QuickCaptureItemViewModel? draggedItem = draggedItems.Count == 1
            ? draggedItems[0]
            : null;
        bool canReorder = draggedItem is not null &&
                          !draggedItem.IsRecent &&
                          (ViewModel.SelectedView is
                              QuickCaptureViewMode.Records or QuickCaptureViewMode.Pinned) &&
                          !ViewModel.HasSearchText;
        if (!QuickCaptureDragPackage.TryPrepare(
                e.Data,
                draggedItems,
                _localizationService))
        {
            e.Cancel = true;
            ResetInternalQuickCaptureDrag();
            return;
        }

        _draggedQuickCaptureItemIds.Clear();
        _draggedQuickCaptureItemIds.AddRange(
            draggedItems.Select(item => item.Id));
        _draggedQuickCaptureItemId = canReorder ? draggedItem!.Id : null;
        _isInternalQuickCaptureDrag = true;
        _internalQuickCaptureDragCanReorder = canReorder;
        _internalQuickCaptureDragView = canReorder
            ? ViewModel.SelectedView
            : null;
        // VisibleItemsSource is an AOT-safe object[] projection. Keep native
        // ListView reordering disabled because WinUI tries to mutate that
        // fixed-size array before DragItemsCompleted is raised. Item-row drop
        // handlers persist the requested position instead.
        ItemsList.CanReorderItems = false;
        e.Data.RequestedOperation =
            DataPackageOperation.Copy |
            DataPackageOperation.Move;
    }

    private void ItemsList_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        ResetInternalQuickCaptureDrag();
    }

    private void QuickCaptureTab_DragOver(object sender, DragEventArgs e)
    {
        if (!_isInternalQuickCaptureDrag ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            !ViewModel.CanApplyTabDrop(GetDraggedQuickCaptureItems(), target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void QuickCaptureTab_Drop(object sender, DragEventArgs e)
    {
        IReadOnlyList<QuickCaptureItemViewModel> draggedItems =
            GetDraggedQuickCaptureItems();
        if (!_isInternalQuickCaptureDrag ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            !ViewModel.CanApplyTabDrop(draggedItems, target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            int changedCount = await ViewModel.ApplyTabDropAsync(
                draggedItems,
                target);
            e.AcceptedOperation = changedCount > 0
                ? DataPackageOperation.Move
                : DataPackageOperation.None;
            if (changedCount > 0)
            {
                ViewModel.SelectedView = target;
                ItemsList.SelectedItems.Clear();
                UpdateSelectedViewVisual();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private IReadOnlyList<QuickCaptureItemViewModel> GetDraggedQuickCaptureItems()
    {
        HashSet<string> draggedIds = _draggedQuickCaptureItemIds.ToHashSet(
            StringComparer.Ordinal);
        return ViewModel.Items
            .Where(item => draggedIds.Contains(item.Id))
            .ToList();
    }

    private static bool TryGetQuickCaptureTabTarget(
        string tag,
        out QuickCaptureViewMode target)
    {
        target = tag switch
        {
            "Pinned" => QuickCaptureViewMode.Pinned,
            "Records" => QuickCaptureViewMode.Records,
            _ => QuickCaptureViewMode.Recent
        };
        return target != QuickCaptureViewMode.Recent;
    }

    private void ResetInternalQuickCaptureDrag()
    {
        _draggedQuickCaptureItemId = null;
        _draggedQuickCaptureItemIds.Clear();
        _internalQuickCaptureDragView = null;
        _internalQuickCaptureDragCanReorder = false;
        _isInternalQuickCaptureDrag = false;
        ItemsList.CanReorderItems = false;
    }

    private static bool IsInteractiveQuickCaptureSource(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is ButtonBase or TextBox)
            {
                return true;
            }

            if (current is FrameworkElement { Name: "QuickCaptureSurfaceItemRoot" })
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool controlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        if (controlPressed && e.Key == Windows.System.VirtualKey.A)
        {
            ItemsList.SelectAll();
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Windows.System.VirtualKey.C)
        {
            await CopySelectedQuickCaptureItemsAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            IReadOnlyList<QuickCaptureItemViewModel> selectedItems =
                GetSelectedQuickCaptureItemsInVisibleOrder();
            if (selectedItems.Count > 0)
            {
                e.Handled = true;
                await DeleteSelectedQuickCaptureItemsAsync(selectedItems);
            }
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && ItemsList.SelectedItems.Count > 0)
        {
            ItemsList.SelectedItems.Clear();
            e.Handled = true;
        }
    }

    private IReadOnlyList<QuickCaptureItemViewModel>
        GetSelectedQuickCaptureItemsInVisibleOrder()
    {
        HashSet<QuickCaptureItemViewModel> selectedItems = ItemsList.SelectedItems
            .OfType<QuickCaptureItemViewModel>()
            .ToHashSet();
        return ViewModel.Items
            .Where(selectedItems.Contains)
            .ToList();
    }

    private async Task CopySelectedQuickCaptureItemsAsync()
    {
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems =
            GetSelectedQuickCaptureItemsInVisibleOrder();
        if (selectedItems.Count == 0)
        {
            return;
        }

        if (selectedItems.Count == 1 &&
            QuickCaptureClipboardCopyPolicy.ShouldCopyBitmap(selectedItems[0]))
        {
            await CopyItemWithFeedbackAsync(selectedItems[0]);
            return;
        }

        string text = selectedItems.Count == 1
            ? QuickCaptureClipboardFormatter.FormatSingle(
                selectedItems[0],
                _localizationService)
            : QuickCaptureClipboardFormatter.FormatBatch(
                selectedItems,
                _localizationService);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var dataPackage = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();
        RaiseFeedback(
            _localizationService.Format(
                "QuickCapture.CopiedCount",
                selectedItems.Count),
            WidgetFeedbackSeverity.Success,
            "quick-copy-selected");
    }

    private async Task DeleteSelectedQuickCaptureItemsAsync(
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems)
    {
        IReadOnlyList<QuickCaptureDeletedItemSnapshot> deletedItems =
            await ViewModel.DeleteItemsAsync(
                selectedItems.Select(item => item.Id),
                selectedItems.All(item => item.IsRecent));
        ItemsList.SelectedItems.Clear();
        if (deletedItems.Count > 0)
        {
            RaiseFeedback(
                _localizationService.Format(
                    "QuickCapture.DeletedCount",
                    deletedItems.Count),
                WidgetFeedbackSeverity.Success,
                "quick-delete-selected",
                T("Common.Undo"),
                async () =>
                {
                    foreach (QuickCaptureDeletedItemSnapshot snapshot in
                             deletedItems.OrderBy(snapshot => snapshot.Item.SortOrder))
                    {
                        await ViewModel.RestoreDeletedItemAsync(snapshot);
                    }

                    await ViewModel.RefreshItemsAsync();
                    ReconcileDetailSelection();
                });
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

        if (DeskBoxDragData.HasDroppedFiles(e.DataView) ||
            e.DataView.Contains(DeskBoxDragData.TextFormat) ||
            e.DataView.Contains(StandardDataFormats.Text) ||
            e.DataView.Contains(StandardDataFormats.WebLink))
        {
            bool hasFiles = DeskBoxDragData.HasDroppedFiles(e.DataView);
            e.AcceptedOperation = hasFiles
                ? DeskBoxDragData.GetFileAssociationOperation(e.DataView)
                : DataPackageOperation.Copy;
            if (hasFiles)
            {
                ApplyFileAssociationDragFeedback(e);
            }
            else
            {
                e.DragUIOverride.IsCaptionVisible = false;
            }
            e.Handled = true;
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
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
                using DroppedFileBatch batch =
                    await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
                QuickCaptureItemViewModel? created =
                    await ViewModel.AddItemWithAttachmentsAsync(batch.Files);
                e.AcceptedOperation = created is null
                    ? DataPackageOperation.None
                    : DeskBoxDragData.GetFileAssociationOperation(e.DataView);
                if (created is not null)
                {
                    RaiseFeedback(
                        T("QuickCapture.Dropped"),
                        WidgetFeedbackSeverity.Success,
                        "quick-drop");
                }
            }
            else
            {
                string? text = await DeskBoxDragData.TryGetTextAsync(
                    e.DataView);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    QuickCaptureWriteResult result = await ViewModel.AddTextAsync(text);
                    ReportBodyTruncation(result);
                    e.AcceptedOperation = DataPackageOperation.Copy;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] Quick Capture drop failed id={WidgetId}: {ex}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-drop-error");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void QuickCaptureItem_DragOver(
        object sender,
        DragEventArgs e)
    {
        if (sender is not Border
            {
                DataContext: QuickCaptureItemViewModel
            } border)
        {
            return;
        }

        if (_isInternalQuickCaptureDrag)
        {
            if (!_internalQuickCaptureDragCanReorder ||
                string.IsNullOrWhiteSpace(_draggedQuickCaptureItemId))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            bool insertAfter = e.GetPosition(border).Y >= border.ActualHeight / 2;
            e.Handled = true;
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.IsGlyphVisible = true;
            ApplyQuickCaptureReorderDropState(border, active: true, insertAfter);
            return;
        }

        if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation =
            DeskBoxDragData.GetFileAssociationOperation(e.DataView);
        ApplyFileAssociationDragFeedback(e);
        ApplyQuickCaptureItemDropState(border, active: true);
    }

    private void ApplyFileAssociationDragFeedback(DragEventArgs e)
    {
        if (!DeskBoxDragData.IsInternalFileDrag(e.DataView))
        {
            SuppressNativeFileDragOverride(e);
            return;
        }

        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption = T(
            "Widget.Compact.QuickCaptureDropHint");
    }

    private static void SuppressNativeFileDragOverride(DragEventArgs e)
    {
        e.DragUIOverride.IsContentVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.IsCaptionVisible = false;
    }

    internal async Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> files,
        QuickCaptureItemViewModel? targetItem)
    {
        if (files.Count == 0)
        {
            return false;
        }

        try
        {
            QuickCaptureItemViewModel? imported = targetItem is null
                ? await ViewModel.AddItemWithAttachmentsAsync(files)
                : await ViewModel.AddAttachmentsAsync(targetItem, files);
            if (imported is null)
            {
                return false;
            }

            RaiseFeedback(
                T("QuickCapture.Dropped"),
                WidgetFeedbackSeverity.Success,
                targetItem is null ? "quick-native-drop" : "quick-native-attach");
            return true;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Quick Capture native file drop failed " +
                $"id={WidgetId}: {ex}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-native-drop-error");
            return false;
        }
    }

    private void QuickCaptureItem_DragLeave(
        object sender,
        DragEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyQuickCaptureReorderDropState(
                border,
                active: false,
                insertAfter: false);
            ApplyQuickCaptureItemDropState(border, active: false);
        }
    }

    private async void QuickCaptureItem_Drop(
        object sender,
        DragEventArgs e)
    {
        if (sender is not Border
            {
                DataContext: QuickCaptureItemViewModel item
            } border)
        {
            return;
        }

        if (_isInternalQuickCaptureDrag)
        {
            await DropQuickCaptureItemAtRowAsync(border, item, e);
            return;
        }

        if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        e.Handled = true;
        ApplyQuickCaptureItemDropState(border, active: false);
        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch =
                await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            QuickCaptureItemViewModel? updated =
                await ViewModel.AddAttachmentsAsync(item, batch.Files);
            e.AcceptedOperation = updated is null
                ? DataPackageOperation.None
                : DeskBoxDragData.GetFileAssociationOperation(e.DataView);
            if (updated is not null)
            {
                RaiseFeedback(
                    T("QuickCapture.Dropped"),
                    WidgetFeedbackSeverity.Success,
                    "quick-attach");
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Quick Capture item drop failed " +
                $"id={WidgetId}: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
            RaiseFeedback(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "quick-attach-error");
        }
        finally
        {
            ApplyQuickCaptureItemDropState(border, active: false);
            deferral.Complete();
        }
    }

    private async Task DropQuickCaptureItemAtRowAsync(
        Border border,
        QuickCaptureItemViewModel targetItem,
        DragEventArgs e)
    {
        string? draggedItemId = _draggedQuickCaptureItemId;
        QuickCaptureViewMode? dragView = _internalQuickCaptureDragView;
        if (!_internalQuickCaptureDragCanReorder ||
            string.IsNullOrWhiteSpace(draggedItemId) ||
            dragView != ViewModel.SelectedView)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        bool insertAfter = e.GetPosition(border).Y >= border.ActualHeight / 2;
        int targetIndex = QuickCaptureDragPackage.ResolveManualDropTargetIndex(
            ViewModel.Items,
            draggedItemId,
            targetItem.Id,
            insertAfter);
        if (targetIndex < 0)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        ApplyQuickCaptureReorderDropState(
            border,
            active: false,
            insertAfter: false);
        var deferral = e.GetDeferral();
        try
        {
            QuickCaptureItemViewModel? draggedItem = ViewModel.Items.FirstOrDefault(
                entry => string.Equals(
                    entry.Id,
                    draggedItemId,
                    StringComparison.Ordinal));
            bool persisted = draggedItem is not null &&
                (dragView == QuickCaptureViewMode.Pinned
                    ? await ViewModel.MovePinnedItemToIndexAsync(
                        draggedItem,
                        targetIndex)
                    : await ViewModel.MoveItemAsync(draggedItem, targetIndex));
            await ViewModel.RefreshItemsAsync();
            e.AcceptedOperation = persisted
                ? DataPackageOperation.Move
                : DataPackageOperation.None;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Quick Capture reorder failed " +
                $"id={WidgetId}: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
            await ViewModel.RefreshItemsAsync();
        }
        finally
        {
            ApplyQuickCaptureReorderDropState(
                border,
                active: false,
                insertAfter: false);
            deferral.Complete();
        }
    }

    private static void ApplyQuickCaptureReorderDropState(
        Border border,
        bool active,
        bool insertAfter)
    {
        border.BorderBrush = active
            ? new SolidColorBrush(
                App.Current.ThemeService?.GetEffectiveAccentColor() ??
                AccentColorHelper.DefaultAccentColor)
            : new SolidColorBrush(Colors.Transparent);
        border.BorderThickness = active
            ? insertAfter
                ? new Thickness(0, 0, 0, 2)
                : new Thickness(0, 2, 0, 0)
            : new Thickness(0);
    }

    private static void ApplyQuickCaptureItemDropState(
        Border border,
        bool active)
    {
        border.Background = active
            ? ResolveBrush(
                "SubtleFillColorSecondaryBrush",
                Color.FromArgb(0x28, 0x78, 0x9E, 0xFF))
            : new SolidColorBrush(Colors.Transparent);
        border.BorderBrush = active
            ? new SolidColorBrush(
                App.Current.ThemeService?.GetEffectiveAccentColor() ??
                AccentColorHelper.DefaultAccentColor)
            : new SolidColorBrush(Colors.Transparent);
        border.BorderThickness = new Thickness(active ? 1 : 0);
    }

    private static Brush ResolveBrush(string key, Color fallback)
    {
        return Application.Current.Resources.TryGetValue(
                   key,
                   out object? value) &&
               value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);
    }

    private void UpdateSelectedViewVisual()
    {
        if (QuickCaptureViewSegmented is null)
        {
            return;
        }

        int selectedIndex = ViewModel.SelectedView switch
        {
            QuickCaptureViewMode.Pinned => 1,
            QuickCaptureViewMode.Recent => 2,
            _ => 0
        };
        if (QuickCaptureViewSegmented.SelectedIndex != selectedIndex)
        {
            bool wasSynchronizing = _isSynchronizingViewSelection;
            _isSynchronizingViewSelection = true;
            try
            {
                QuickCaptureViewSegmented.SelectedIndex = selectedIndex;
            }
            finally
            {
                _isSynchronizingViewSelection = wasSynchronizing;
            }
        }
    }

    private void QuickCaptureItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        SetQuickCaptureItemActionButtonsVisible(border, false);
        ApplyQuickCaptureItemMaterialSurface(
            border,
            border.DataContext as QuickCaptureItemViewModel);
    }

    private async void QuickCaptureAttachmentPreview_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureQuickCaptureAttachmentThumbnailAsync(
            (sender as FrameworkElement)?.DataContext);
    }

    private async void QuickCaptureAttachmentPreview_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        await EnsureQuickCaptureAttachmentThumbnailAsync(args.NewValue);
    }

    private static async Task EnsureQuickCaptureAttachmentThumbnailAsync(object? dataContext)
    {
        if (dataContext is TodoAttachmentViewModel attachment)
        {
            await attachment.EnsureThumbnailAsync();
        }
    }

    private void QuickCaptureItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetQuickCaptureItemActionButtonsVisible(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetQuickCaptureItemActionButtonsVisible(sender as DependencyObject, false);
    }

    private static void SetQuickCaptureItemActionButtonsVisible(
        DependencyObject? itemRoot,
        bool isVisible)
    {
        if (itemRoot is null ||
            FindQuickCaptureVisualChild<Border>(itemRoot, "QuickCaptureItemActionButtons") is not { } actionButtons)
        {
            return;
        }

        actionButtons.Opacity = isVisible ? 1 : 0;
        actionButtons.IsHitTestVisible = isVisible;
    }

    private static T? FindQuickCaptureVisualChild<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal))
            {
                return typed;
            }

            if (FindQuickCaptureVisualChild<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void QuickCaptureItem_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (sender is Border border)
        {
            SetQuickCaptureItemActionButtonsVisible(border, false);
            // ListView virtualizes and reuses this Border. Reapply the
            // material for every new item so clipboard entries cannot inherit
            // a colored record background from the previous DataContext.
            ApplyQuickCaptureItemMaterialSurface(
                border,
                args.NewValue as QuickCaptureItemViewModel);
        }
    }

    private void QuickCaptureSurfaceContent_ActualThemeChanged(
        FrameworkElement sender,
        object args)
    {
        if (_isDisposed)
        {
            return;
        }

        ApplyDetailMaterialSurface();
        RefreshItemMaterialSurfaces();
    }

    private void RefreshItemMaterialSurfaces()
    {
        if (_isDisposed || ItemsList is null)
        {
            return;
        }

        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            if (ItemsList.ContainerFromItem(item) is ListViewItem
                {
                    ContentTemplateRoot: Border border
                })
            {
                ApplyQuickCaptureItemMaterialSurface(border, item);
            }
        }
    }

    private void ApplyQuickCaptureItemMaterialSurface(
        Border border,
        QuickCaptureItemViewModel? item)
    {
        if (item is null)
        {
            border.Background = ResolveMaterialBrush(
                QuickCaptureAppearancePreset.Default);
            return;
        }

        QuickCaptureAppearancePreset requestedPreset =
            _detailHasUnsavedChanges &&
            string.Equals(_detailItem?.Id, item.Id, StringComparison.Ordinal)
                ? _detailAppearance
                : item.AppearancePreset;
        QuickCaptureAppearancePreset preset =
            QuickCaptureAppearancePolicy.ResolveListPreset(
                requestedPreset,
                item.IsRecent);
        border.Background = ResolveMaterialBrush(preset);
    }

    private Brush ResolveMaterialBrush(QuickCaptureAppearancePreset preset)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        return preset switch
        {
            QuickCaptureAppearancePreset.Paper => new SolidColorBrush(
                dark ? Color.FromArgb(0xB8, 0x3A, 0x36, 0x30) : Color.FromArgb(0xEC, 0xFA, 0xF5, 0xEA)),
            QuickCaptureAppearancePreset.StickyYellow => new SolidColorBrush(
                dark ? Color.FromArgb(0xB8, 0x4A, 0x40, 0x25) : Color.FromArgb(0xEC, 0xFF, 0xF0, 0xB3)),
            QuickCaptureAppearancePreset.Rose => new SolidColorBrush(
                dark ? Color.FromArgb(0xB8, 0x47, 0x2E, 0x38) : Color.FromArgb(0xEC, 0xFC, 0xE3, 0xEA)),
            QuickCaptureAppearancePreset.Mint => new SolidColorBrush(
                dark ? Color.FromArgb(0xB8, 0x28, 0x42, 0x35) : Color.FromArgb(0xEC, 0xDD, 0xF3, 0xE3)),
            QuickCaptureAppearancePreset.MistBlue => new SolidColorBrush(
                dark ? Color.FromArgb(0xB8, 0x2B, 0x3D, 0x53) : Color.FromArgb(0xEC, 0xDF, 0xEC, 0xF8)),
            _ => ResolveBrush(
                "CardBackgroundFillColorDefaultBrush",
                Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF))
        };
    }

    private async void QuickCaptureViewSegmented_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isSynchronizingViewSelection || !IsLoaded || ItemsList is null)
        {
            return;
        }

        if (QuickCaptureViewSegmented.Visibility != Visibility.Visible)
        {
            // Template realization may briefly report its default index while
            // the tab strip is hidden. Reassert the member state without
            // treating that programmatic event as a user action.
            UpdateSelectedViewVisual();
            return;
        }

        QuickCaptureViewMode mode = QuickCaptureViewSegmented.SelectedIndex switch
        {
            1 => QuickCaptureViewMode.Pinned,
            2 => QuickCaptureViewMode.Recent,
            _ => QuickCaptureViewMode.Records
        };

        await SwitchViewAsync(mode);
    }

    private async Task SwitchViewAsync(QuickCaptureViewMode mode)
    {
        long revision = ++_viewSwitchRevision;
        if (ViewModel.SelectedView == mode)
        {
            UpdateSelectedViewVisual();
            return;
        }

        await FlushPendingDetailSaveAsync();
        if (_isDisposed || revision != _viewSwitchRevision)
        {
            return;
        }

        if (_detailHasUnsavedChanges)
        {
            UpdateSelectedViewVisual();
            return;
        }

        ClearDetailForViewChange();
        ViewModel.SelectedView = mode;
        ItemsList.SelectedItems.Clear();
        RefreshDetailPresentation();
        UpdateSelectedViewVisual();
    }

    private void ClearDetailForViewChange()
    {
        _detailAutoSaveTimer?.Stop();
        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            item.IsDetailSelected = false;
        }

        _detailItem = null;
        _isCreatingDetail = false;
        _isDetailEditing = false;
        _detailHasUnsavedChanges = false;
        _detailEditRevision = 0;
        _detailSavedRevision = 0;
        _showDetailInSinglePane = false;
        _pendingDetailAttachments.Clear();
        SetDetailEditorText(string.Empty);
        DetailMarkdownView.Markdown = string.Empty;
        RefreshDetailAttachments();
        ApplyResponsiveLayout();
        RefreshDetailPresentation();
    }

    private void QuickCaptureViewSegmented_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplySegmentedStyle();
        }
    }

    private void ApplySegmentedStyle()
    {
        if (QuickCaptureViewSegmented is null)
        {
            return;
        }

        WidgetSegmentedStyleHelper.Apply(
            QuickCaptureViewSegmented,
            ViewModel.TabStyle);
        if (ViewModel.TabStyle == SettingsService.WidgetTabStyleButton)
        {
            WidgetSegmentedLayoutHelper.ApplyEqualItemWidths(QuickCaptureViewSegmented);
        }
        else
        {
            WidgetSegmentedLayoutHelper.ApplyNaturalItemWidths(QuickCaptureViewSegmented);
        }
    }

    private string GetCurrentFocusTarget()
    {
        object? focused = XamlRoot is null
            ? null
            : FocusManager.GetFocusedElement(XamlRoot);
        if (ReferenceEquals(focused, InputTextBox))
        {
            return "Input";
        }
        if (ReferenceEquals(focused, SearchTextBox))
        {
            return "Search";
        }
        if (ReferenceEquals(focused, ItemsList))
        {
            return "Items";
        }

        return "Root";
    }

    private void ApplyPendingFocus()
    {
        string target = _pendingFocusTarget ?? _lastFocusTarget;
        _pendingFocusTarget = null;
        FrameworkElement element = target switch
        {
            "Input" => InputTextBox,
            "Search" => SearchTextBox,
            "Items" => ItemsList,
            _ => ResponsiveContentGrid
        };
        element.Focus(FocusState.Programmatic);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] Quick Capture action failed id={WidgetId}: {ex}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-action-error");
        }
    }

    private async Task AddInputWithFeedbackAsync()
    {
        QuickCaptureWriteResult result = await ViewModel.AddInputAsync();
        ReportBodyTruncation(result);
    }

    private void ReportBodyTruncation(QuickCaptureWriteResult result)
    {
        if (!result.WasTruncated)
        {
            return;
        }

        RaiseFeedback(
            T("QuickCapture.BodyTruncated"),
            WidgetFeedbackSeverity.Warning,
            "quick-body-truncated");
    }

    private void DetailMarkdownEditor_TextTruncated(object? sender, EventArgs e)
    {
        RaiseFeedback(
            T("QuickCapture.BodyTruncated"),
            WidgetFeedbackSeverity.Warning,
            "quick-body-truncated");
    }

    private void RaiseFeedback(
        string message,
        WidgetFeedbackSeverity severity,
        string deduplicationKey,
        string? actionText = null,
        Func<Task>? action = null)
    {
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(
                new WidgetFeedbackRequest(
                    message,
                    severity,
                    deduplicationKey,
                    actionText,
                    action)));
    }

    private string T(string key) =>
        _localizationService.T(key);

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _detailImageLoadVersion++;
        DetailPrimaryImage.Source = null;
        CancelSegmentedRestore();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ActualThemeChanged -= QuickCaptureSurfaceContent_ActualThemeChanged;
        if (_detailAutoSaveTimer is not null)
        {
            _detailAutoSaveTimer.Stop();
            _detailAutoSaveTimer.Tick -= DetailAutoSaveTimer_Tick;
            _detailAutoSaveTimer = null;
        }
        DetailMarkdownEditor.EditorTextChanged -= DetailMarkdownEditor_EditorTextChanged;
        DetailMarkdownEditor.TextTruncated -= DetailMarkdownEditor_TextTruncated;
        DetailMarkdownEditor.CommitRequested -= DetailMarkdownEditor_CommitRequested;
        DetailMarkdownView.AttachmentOpenRequested -= DetailMarkdownView_AttachmentOpenRequested;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Dispose();
    }
}
