using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
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
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Window-independent Quick Capture leaf view. All top-level window, DWM,
/// z-order, bounds, capsule, and group navigation behavior stays with the
/// surface host; this control owns only Quick Capture interaction state.
/// The residency adapter (QuickCaptureWidgetContentAdapter) owns the view
/// model and this leaf's lifetime.
/// </summary>
public sealed partial class QuickCaptureSurfaceContent :
    UserControl,
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
    private bool _pendingDetailWasDualPane;
    private bool _pendingCreatingDetail;
    private bool _pendingDetailHasAppearanceOverride;
    private QuickCaptureAppearancePreset _pendingDetailAppearance;
    private TextContentFormat _pendingDetailContentFormat = TextContentFormat.Markdown;
    private DroppedFilePath[] _pendingRestoredDetailAttachments = [];
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
    private long _detailPrimaryImageEstimatedBytes;
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
    private bool _isCompactCollapsed;
    private bool _wasDualPaneBeforeCollapse;
    private bool _isDisposed;
    private bool _isInitialized;
    private bool _isWindowVisible;
    private bool _isWindowRevealCompleted;
    private bool _isWindowActivated;
    private bool _detailMarkdownEditorWired;
    private Func<Task>? _lastUndoAction;
    private CancellationTokenSource? _pendingSnapshotCleanupCts;
    private IReadOnlyList<QuickCaptureDeletedItemSnapshot> _pendingCleanupSnapshots = [];
    private KeyEventHandler? _surfaceKeyDownHandler;
    private PointerEventHandler? _surfacePointerPressedHandler;

    public QuickCaptureSurfaceContent(
        QuickCaptureWidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(dispatcherQueue);

        _localizationService = localizationService;
        _settingsService = settingsService;
        ViewModel = viewModel;
        WidgetConfig config = viewModel.Config;

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
        if (DragDropPermissionService.IsCurrentProcessElevatedCached())
        {
            ItemsList.CanDragItems = false;
        }
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        if (config.Metadata.TryGetValue(MasterPaneWidthMetadataKey, out string? persisted) &&
            double.TryParse(persisted, NumberStyles.Float, CultureInfo.InvariantCulture, out double width))
        {
            _persistedMasterPaneWidth = _masterDetailLayoutPolicy.NormalizePersistedMasterWidth(width);
        }
        DetailMarkdownView.AttachmentResolver = ResolveDetailAttachmentPath;
        DetailMarkdownView.AttachmentOpenRequested += DetailMarkdownView_AttachmentOpenRequested;
        _detailBodyReaderDoubleTappedHandler = new DoubleTappedEventHandler(DetailBodyReaderSurface_DoubleTapped);
        DetailBodyReaderSurface.AddHandler(
            UIElement.DoubleTappedEvent,
            _detailBodyReaderDoubleTappedHandler,
            handledEventsToo: true);
        _surfaceKeyDownHandler = new KeyEventHandler(Surface_KeyDown);
        ResponsiveContentGrid.AddHandler(
            UIElement.KeyDownEvent,
            _surfaceKeyDownHandler,
            handledEventsToo: false);
        _surfacePointerPressedHandler = new PointerEventHandler(Surface_PointerPressed);
        ResponsiveContentGrid.AddHandler(
            UIElement.PointerPressedEvent,
            _surfacePointerPressedHandler,
            handledEventsToo: true);
        _detailAutoSaveTimer = dispatcherQueue.CreateTimer();
        _detailAutoSaveTimer.Interval = TimeSpan.FromMilliseconds(DetailAutoSaveDelayMs);
        _detailAutoSaveTimer.IsRepeating = false;
        _detailAutoSaveTimer.Tick += DetailAutoSaveTimer_Tick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += QuickCaptureSurfaceContent_ActualThemeChanged;
        InitializeOptimizationHooks(dispatcherQueue);
        UpdateSelectedViewVisual();
    }

    public QuickCaptureWidgetViewModel ViewModel { get; }

    internal event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    internal WidgetConfig Config => ViewModel.Config;

    internal string WidgetId => Config.Id;

    internal bool IsReadyForReuse => _isInitialized && !_isDisposed;

    internal async Task InitializeContentAsync()
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

    internal void SyncFromResidentViewModel()
    {
        if (_isDisposed || !ViewModel.IsInitialized)
        {
            return;
        }

        _isInitialized = true;
        UpdateSelectedViewVisual();
        ApplyResponsiveLayout();
        if (!RestorePendingDetailState())
        {
            ReconcileDetailSelection();
        }
    }

    internal async Task AddFromTitleButtonAsync()
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

    internal void ApplyAppearance()
    {
        ViewModel.ApplyAppearancePreview();
        UpdateSelectedViewVisual();
        ApplySegmentedStyle();
        ApplyResponsiveLayout();
    }

    internal void OnActivated()
    {
        _isWindowActivated = true;
        UpdateOwnerFocusSelectionVisuals();
        if (IsLoaded)
        {
            ApplyPendingFocus();
        }

        DispatcherQueue.TryEnqueue(UpdateOwnerFocusSelectionVisuals);
    }

    internal async void OnDeactivated()
    {
        _isWindowActivated = false;
        UpdateOwnerFocusSelectionVisuals();
        DispatcherQueue.TryEnqueue(UpdateOwnerFocusSelectionVisuals);
        _lastFocusTarget = GetCurrentFocusTarget();
        await FlushPendingDetailSaveAsync();
    }

    internal async void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (!visible)
        {
            _isWindowRevealCompleted = false;
            ViewModel.SuspendWindowRefresh();
            await FlushPendingDetailSaveAsync();
        }
    }

    internal void OnWindowRevealCompleted()
    {
        if (!_isWindowVisible || _isWindowRevealCompleted)
        {
            return;
        }

        _isWindowRevealCompleted = true;
        if (IsLoaded)
        {
            ViewModel.RefreshAfterViewReady();
            RehydrateRealizedAttachmentThumbnails();
        }
    }

    internal void RestoreTransientState(string? inputText, string? searchText)
    {
        ViewModel.InputText = inputText ?? string.Empty;
        ViewModel.SearchText = searchText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            ViewModel.ExpandSearch();
        }
    }

    internal object? CaptureSwitchTransientState()
    {
        bool effectiveDualPane = _isCompactCollapsed
            ? (_isDualPane || _wasDualPaneBeforeCollapse)
            : _isDualPane;
        bool isCreatingWithContent = _isCreatingDetail && HasNewDetailContent();
        bool hasDetailToCapture = isCreatingWithContent || (!_isCreatingDetail && _detailItem is not null);
        bool shouldCaptureDetail =
            QuickCaptureDetailRestorePolicy.ShouldCaptureDetail(
                effectiveDualPane,
                _showDetailInSinglePane,
                hasDetailToCapture);
        bool captureEditing = shouldCaptureDetail && (isCreatingWithContent || _isDetailEditing);
        var baseState = new QuickCaptureWidgetTransientState(
            ViewModel.InputText,
            ViewModel.SearchText,
            ViewModel.SelectedView,
            _lastFocusTarget,
            shouldCaptureDetail && !isCreatingWithContent ? _detailItem?.Id : null,
            captureEditing,
            captureEditing
                ? (DetailMarkdownEditor?.Text ?? string.Empty)
                : null,
            shouldCaptureDetail && !effectiveDualPane && _showDetailInSinglePane);
        var extendedState = new QuickCaptureExtendedTransientState(
            baseState,
            WasDualPane: effectiveDualPane,
            IsCreatingDetail: shouldCaptureDetail && isCreatingWithContent,
            DetailAppearance: _detailAppearance,
            DetailContentFormat: _detailContentFormat,
            PendingDetailAttachments: shouldCaptureDetail && isCreatingWithContent && _pendingDetailAttachments.Count > 0
                ? _pendingDetailAttachments.ToArray()
                : null,
            HasAppearanceOverride: shouldCaptureDetail && (isCreatingWithContent || _isDetailEditing || _detailHasUnsavedChanges));
        string? inlineDraft = InlineComposerTextBox?.Text;
        bool hasInlineDraft = !string.IsNullOrWhiteSpace(inlineDraft) || _pendingInlineAttachments.Count > 0;
        return new QuickCaptureSurfaceSwitchState(
            extendedState,
            IsInlineComposerOpen: _isInlineComposerOpen || hasInlineDraft,
            InlineComposerDraft: !string.IsNullOrEmpty(inlineDraft) ? inlineDraft : null,
            PendingInlineAttachments: _pendingInlineAttachments.Count > 0
                ? _pendingInlineAttachments.ToArray()
                : null);
    }

    internal void RestoreSwitchTransientState(object? state)
    {
        QuickCaptureSurfaceSwitchState? switchState = state as QuickCaptureSurfaceSwitchState;
        QuickCaptureExtendedTransientState? extendedState =
            switchState?.ExtendedState ?? state as QuickCaptureExtendedTransientState;
        QuickCaptureWidgetTransientState? resolvedState =
            extendedState?.BaseState ?? state as QuickCaptureWidgetTransientState;
        if (resolvedState is QuickCaptureWidgetTransientState quickState)
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
            _pendingDetailWasDualPane = extendedState?.WasDualPane ?? false;
            _pendingCreatingDetail = extendedState?.IsCreatingDetail ?? false;
            _pendingDetailAppearance = extendedState?.DetailAppearance ?? QuickCaptureAppearancePreset.Default;
            _pendingDetailContentFormat = extendedState?.DetailContentFormat ?? ViewModel.EditorContentFormat;
            _pendingDetailHasAppearanceOverride = extendedState?.HasAppearanceOverride ?? false;
            _pendingRestoredDetailAttachments = extendedState?.PendingDetailAttachments ?? [];
            RestoreInlineComposerTransientState(
                switchState?.IsInlineComposerOpen ?? false,
                switchState?.InlineComposerDraft,
                switchState?.PendingInlineAttachments);
            UpdateSelectedViewVisual();
            if (IsLoaded && _isInitialized)
            {
                RestorePendingDetailState();
                DispatcherQueue.TryEnqueue(ApplyPendingFocus);
            }
        }
    }

    private bool HasPendingDetailRestoreState() =>
        !string.IsNullOrWhiteSpace(_pendingDetailItemId) || _pendingCreatingDetail;

    private bool HasMeasuredExpandedViewport()
    {
        if (_isCompactCollapsed && !_isResponsiveLayoutTransitionActive)
        {
            return false;
        }

        if (double.IsFinite(_hostViewportWidth) && _hostViewportWidth > 0)
        {
            return true;
        }

        return !_pendingDetailWasDualPane &&
               IsLoaded &&
               ResponsiveContentGrid.ActualWidth > 0;
    }

    private void ClearPendingDetailRestoreFields()
    {
        _pendingDetailItemId = null;
        _pendingDetailEditing = false;
        _pendingDetailDraft = null;
        _pendingDetailWasVisibleInSinglePane = false;
        _pendingDetailWasDualPane = false;
        _pendingCreatingDetail = false;
        _pendingDetailHasAppearanceOverride = false;
        _pendingRestoredDetailAttachments = [];
    }

    private bool RestorePendingDetailState()
    {
        bool hasPendingItem = !string.IsNullOrWhiteSpace(_pendingDetailItemId);
        bool hasPendingCreate = _pendingCreatingDetail;
        if (!hasPendingItem && !hasPendingCreate)
        {
            ClearPendingDetailRestoreFields();
            return false;
        }

        if (!QuickCaptureDetailRestorePolicy.ShouldRestoreDetail(
                _isDualPane,
                _pendingDetailWasVisibleInSinglePane))
        {
            if (!HasMeasuredExpandedViewport())
            {
                return false;
            }

            ClearPendingDetailRestoreFields();
            return false;
        }

        if (hasPendingCreate)
        {
            string draftBody = _pendingDetailDraft ?? string.Empty;
            QuickCaptureAppearancePreset draftAppearance = _pendingDetailAppearance;
            TextContentFormat draftFormat = _pendingDetailContentFormat;
            DroppedFilePath[] draftAttachments = _pendingRestoredDetailAttachments;
            bool showInSinglePane = !_isDualPane && _pendingDetailWasVisibleInSinglePane;
            ClearPendingDetailRestoreFields();

            _detailAutoSaveTimer?.Stop();
            _isCreatingDetail = true;
            _detailItem = null;
            _detailAppearance = draftAppearance;
            _detailContentFormat = draftFormat;
            _pendingDetailAttachments.Clear();
            _pendingDetailAttachments.AddRange(draftAttachments);
            _isDetailEditing = true;
            _detailEditRevision = 1;
            _detailSavedRevision = 0;
            _detailHasUnsavedChanges =
                !string.IsNullOrWhiteSpace(draftBody) || _pendingDetailAttachments.Count > 0;
            _showDetailInSinglePane = showInSinglePane;
            MarkdownSourceEditor draftEditor = EnsureDetailMarkdownEditorRealized();
            SetDetailEditorText(draftBody);
            draftEditor.ShowFormattingToolbar =
                _detailContentFormat == TextContentFormat.Markdown;
            DetailTimestampText.Text = _localizationService.Format(
                "QuickCapture.Detail.Created",
                DateTimeOffset.Now.ToString("yyyy/M/d HH:mm"));
            RefreshDetailAttachments();
            ApplyDetailMaterialSurface();
            if (showInSinglePane)
            {
                ApplyResponsiveLayout();
            }
            RefreshDetailPresentation();
            return true;
        }

        QuickCaptureItemViewModel? item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _pendingDetailItemId, StringComparison.Ordinal));
        bool pendingEditing = _pendingDetailEditing;
        string? pendingDraft = _pendingDetailDraft;
        bool hasAppearanceOverride = _pendingDetailHasAppearanceOverride;
        QuickCaptureAppearancePreset pendingAppearance = _pendingDetailAppearance;
        TextContentFormat pendingFormat = _pendingDetailContentFormat;
        ClearPendingDetailRestoreFields();
        if (item is null)
        {
            return false;
        }

        OpenDetail(item);
        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
        if (pendingEditing)
        {
            BeginDetailEditing();
            bool dirty = false;
            if (hasAppearanceOverride &&
                (pendingAppearance != item.AppearancePreset ||
                 pendingFormat != _detailContentFormat))
            {
                _detailAppearance = pendingAppearance;
                _detailContentFormat = pendingFormat;
                ApplyDetailMaterialSurface();
                dirty = true;
            }

            if (pendingDraft is { } draft &&
                !string.Equals(draft, item.Body, StringComparison.Ordinal))
            {
                SetDetailEditorText(draft);
                dirty = true;
            }

            if (dirty)
            {
                MarkDetailDirty();
                if (!string.IsNullOrWhiteSpace(DetailMarkdownEditor?.Text))
                {
                    _detailAutoSaveTimer?.Start();
                }
            }

            RefreshDetailPresentation();
        }
        else if (_isDetailEditing)
        {
            _isDetailEditing = false;
            _detailContentFormat = item.ContentFormat;
            SetDetailEditorText(string.Empty);
            RefreshDetailPresentation();
        }

        return true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartRelativeTimestampTimer();
        RebuildFilterChipBar();
        UpdateInlineComposerPresentation();
        if (_isWindowRevealCompleted)
        {
            ViewModel.RefreshAfterViewReady();
        }
        UpdateSelectedViewVisual();
        ApplyPendingFocus();
        ApplyResponsiveLayout();
        if (_isInitialized && !_isCreatingDetail)
        {
            if (!RestorePendingDetailState())
            {
                ReconcileDetailSelection();
            }
        }
        RefreshItemMaterialSurfaces();
        QueueSegmentedRestore();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRelativeTimestampTimer();
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
        if (!_isInteractiveResizeActive &&
            (!_isCompactCollapsed || _isResponsiveLayoutTransitionActive))
        {
            ApplyResponsiveLayout();
        }
    }

    internal void BeginInteractiveResize(double contentWidth, double contentHeight)
    {
        _isInteractiveResizeActive = true;
        CancelSegmentedRestore();
        _deferDetailReaderUntilTransitionCompletes = true;
    }

    internal void CompleteInteractiveResize(double contentWidth, double contentHeight)
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
            PaneSplitter is null ||
            (_isCompactCollapsed && !_isResponsiveLayoutTransitionActive))
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
                if (!RestorePendingDetailState())
                {
                    ReconcileDetailSelection();
                }
            }
        }
        else
        {
            if (HasPendingDetailRestoreState() && HasMeasuredExpandedViewport())
            {
                if (RestorePendingDetailState())
                {
                    return;
                }
            }

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

    internal void OnCompactStateChanged(bool collapsed)
    {
        if (collapsed)
        {
            if (!_isCompactCollapsed)
            {
                _wasDualPaneBeforeCollapse = _isDualPane;
                _isCompactCollapsed = true;
            }

            CancelSegmentedRestore();
            return;
        }

        _isCompactCollapsed = false;
        _wasDualPaneBeforeCollapse = false;
        if (double.IsFinite(_hostViewportWidth) && _hostViewportWidth > 0)
        {
            ApplyResponsiveLayout();
            QueueSegmentedRestore();
        }

        RehydrateRealizedAttachmentThumbnails();
    }

    private void RehydrateRealizedAttachmentThumbnails()
    {
        if (_isDisposed || !IsLoaded || _isCompactCollapsed)
        {
            return;
        }

        if (ItemsList.ItemsPanelRoot is Panel panel)
        {
            foreach (UIElement element in panel.Children)
            {
                if (element is ListViewItem container &&
                    ItemsList.ItemFromContainer(container) is QuickCaptureItemViewModel liveItem &&
                    liveItem.PrimaryImageAttachment is { } primaryAttachment)
                {
                    _ = EnsureQuickCaptureAttachmentThumbnailAsync(primaryAttachment);
                }
            }
        }

        if (_detailItem is { } detail)
        {
            foreach (TodoAttachmentViewModel attachment in detail.Attachments)
            {
                _ = EnsureQuickCaptureAttachmentThumbnailAsync(attachment);
            }
        }
    }

    internal void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        if (isCollapsing)
        {
            if (!_isCompactCollapsed)
            {
                _wasDualPaneBeforeCollapse = _isDualPane;
                _isCompactCollapsed = true;
            }
        }
        else
        {
            _isCompactCollapsed = false;
            _wasDualPaneBeforeCollapse = false;
        }

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
            if (_isDetailEditing)
            {
                _detailContentFormat = ViewModel.EditorContentFormat;
                RefreshDetailPresentation();
            }
            return;
        }

        if (e.PropertyName is nameof(QuickCaptureWidgetViewModel.InputAreaVisibility)
            or nameof(QuickCaptureWidgetViewModel.ContentTextSize)
            or nameof(QuickCaptureWidgetViewModel.EditorEnterBehavior))
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateOptimizationLocalizedChrome();
                UpdateInlineComposerPresentation();
            }
            else
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isDisposed)
                    {
                        UpdateOptimizationLocalizedChrome();
                        UpdateInlineComposerPresentation();
                    }
                });
            }
            return;
        }

        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.FilterChips))
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                RebuildFilterChipBar();
            }
            else
            {
                DispatcherQueue.TryEnqueue(RebuildFilterChipBar);
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

    internal void OnHostViewportSizeChanged(double width, double height)
    {
        if (!double.IsFinite(width) ||
            width <= 0 ||
            (_isCompactCollapsed && !_isResponsiveLayoutTransitionActive))
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

    internal void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        _isResponsiveLayoutTransitionActive = false;
        _deferDetailReaderUntilTransitionCompletes = false;
        if (_isCompactCollapsed)
        {
            return;
        }

        if (double.IsFinite(finalContentWidth) && finalContentWidth > 0)
        {
            _hostViewportWidth = finalContentWidth;
            Width = finalContentWidth;
        }

        ApplyResponsiveLayout();
        QueueSegmentedRestore();
    }

    internal void CancelResponsiveLayoutTransition()
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
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

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
        {
            if (_isDisposed)
            {
                return;
            }

            if (ItemsList.Visibility == Visibility.Visible)
            {
                ItemsList.Focus(FocusState.Programmatic);
            }
            else
            {
                SearchButton.Focus(FocusState.Programmatic);
            }
        });
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

        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "quick-capture-item-click");
        UpdateOwnerFocusSelectionVisuals();

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            return;
        }

        await OpenDetailAfterSavingAsync(item);
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        ExpandAndFocusSearch();
    }

    private void ExpandAndFocusSearch()
    {
        bool wasExpanded = ViewModel.IsSearchExpanded;
        ViewModel.ExpandSearch();
        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "quick-capture-search");
        DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(SearchTextBox);
        if (!wasExpanded)
        {
            PlayExpandEntranceAnimation(SearchTextBox);
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed && ViewModel.IsSearchExpanded)
            {
                SearchTextBox.Focus(FocusState.Programmatic);
                SearchTextBox.SelectAll();
            }
        });
    }

    private async void AddNoteCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, AddNoteCardButton))
        {
            OpenInlineComposer();
            return;
        }

        await OpenNewDetailAsync();
    }

    private async void AddNoteCardButtonFallback_Click(object sender, RoutedEventArgs e)
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
        bool wasShowingSinglePaneDetail = _showDetailInSinglePane;
        _showDetailInSinglePane = !_isDualPane;
        MarkdownSourceEditor newDetailEditor = EnsureDetailMarkdownEditorRealized();
        SetDetailEditorText(string.Empty);
        newDetailEditor.ShowFormattingToolbar =
            _detailContentFormat == TextContentFormat.Markdown;
        DetailTimestampText.Text = _localizationService.Format(
            "QuickCapture.Detail.Created",
            DateTimeOffset.Now.ToString("yyyy/M/d HH:mm"));
        RefreshDetailAttachments();
        ApplyDetailMaterialSurface();
        ApplyResponsiveLayout();
        RefreshDetailPresentation();
        if (!_isDualPane && !wasShowingSinglePaneDetail)
        {
            PlaySinglePaneNavigationTransition(enteringDetail: true);
        }

        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "quick-capture-new-detail");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed && _isDetailEditing)
            {
                EnsureDetailMarkdownEditorRealized().FocusEditor(moveCaretToEnd: false);
            }
        });
    }

    private void OpenDetail(QuickCaptureItemViewModel item)
    {
        _detailAutoSaveTimer?.Stop();
        _isCreatingDetail = false;
        _detailItem = item;
        _detailAppearance = item.AppearancePreset;
        _pendingDetailAttachments.Clear();
        bool isEmptyNote = string.IsNullOrWhiteSpace(item.Body) && item.Attachments.Count == 0;
        _isDetailEditing =
            isEmptyNote ||
            (_isDualPane && SettingsService.NormalizeQuickCaptureWideOpenMode(
                _settingsService.Settings.QuickCaptureWideOpenMode) ==
                SettingsService.QuickCaptureWideOpenEditing);
        _detailContentFormat = _isDetailEditing
            ? ViewModel.EditorContentFormat
            : item.ContentFormat;
        _detailEditRevision = 0;
        _detailSavedRevision = 0;
        _detailHasUnsavedChanges = false;
        _showDetailInSinglePane = !_isDualPane;
        if (_isDetailEditing)
        {
            _ = EnsureDetailMarkdownEditorRealized();
        }
        SetDetailEditorText(_isDetailEditing ? item.Body : string.Empty);
        if (DetailMarkdownEditor is not null)
        {
            DetailMarkdownEditor.ShowFormattingToolbar =
                _detailContentFormat == TextContentFormat.Markdown;
        }
        DetailMarkdownView.Markdown = item.Body;
        DetailMarkdownView.ContentFormat = _detailContentFormat;
        DetailMarkdownView.AllowRemoteImages =
            _settingsService.Settings.QuickCaptureAllowRemoteImages;
        DetailMarkdownView.AreTaskListsInteractive =
            _detailContentFormat == TextContentFormat.Markdown;
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
        bool wasShowingDetailInSinglePane = _showDetailInSinglePane;
        if (_detailItem is not null &&
            string.Equals(_detailItem.Id, item.Id, StringComparison.Ordinal))
        {
            if (!_isDualPane)
            {
                _showDetailInSinglePane = true;
                RefreshDetailAttachments();
                ApplyResponsiveLayout();
                RefreshDetailPresentation();
                if (!wasShowingDetailInSinglePane)
                {
                    PlaySinglePaneNavigationTransition(enteringDetail: true);
                }
            }
            return;
        }

        await FlushPendingDetailSaveAsync();
        if (_detailHasUnsavedChanges)
        {
            return;
        }

        OpenDetail(item);
        if (!_isDualPane && !wasShowingDetailInSinglePane)
        {
            PlaySinglePaneNavigationTransition(enteringDetail: true);
        }
    }

    private void RefreshDetailPresentation()
    {
        bool hasDetail = _isCreatingDetail || _detailItem is not null;
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
        DetailEditButton.Visibility = hasDetail && !_isDetailEditing
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailDoneButton.Visibility = hasDetail && _isDetailEditing
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_isDualPane)
        {
            DetailBackColumn.Width = new GridLength(8);
        }
        DetailPopoutButton.Visibility = _detailItem is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailMoreButton.Visibility = hasDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailPinButton.Visibility = _detailItem is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailCopyButton.Visibility = _detailItem is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailDeleteButton.Visibility = _detailItem is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailAddFileButton.Visibility = hasDetail && _isDetailEditing
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailAttachmentStrip.CanRemove = hasDetail && _isDetailEditing;
        bool hasPrimaryImage = !string.IsNullOrWhiteSpace(_detailPrimaryImagePath);
        bool showDetailText = _isDetailEditing ||
                              !hasPrimaryImage ||
                              HasMeaningfulDetailText();
        ApplyDetailPrimaryImageLayout(hasPrimaryImage, showDetailText);
        bool showDetailEditor = hasDetail &&
                                _isDetailEditing &&
                                showDetailText;
        if (showDetailEditor)
        {
            EnsureDetailMarkdownEditorRealized().Visibility = Visibility.Visible;
        }
        else if (DetailMarkdownEditor is not null)
        {
            DetailMarkdownEditor.Visibility = Visibility.Collapsed;
        }
        DetailMarkdownView.Visibility = hasDetail &&
                                        !_isDetailEditing &&
                                        !_deferDetailReaderUntilTransitionCompletes &&
                                        showDetailText
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailMaterialPalette.Visibility = hasDetail && _isDetailEditing
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (hasDetail)
        {
            DetailMarkdownView.Markdown = GetDetailPresentationBody();
            DetailMarkdownView.ContentFormat = _detailContentFormat;
            DetailMarkdownView.AllowRemoteImages =
                _settingsService.Settings.QuickCaptureAllowRemoteImages;
            DetailMarkdownView.AreTaskListsInteractive =
                _detailContentFormat == TextContentFormat.Markdown;
            if (_isDetailEditing)
            {
                EnsureDetailMarkdownEditorRealized().ShowFormattingToolbar =
                    _detailContentFormat == TextContentFormat.Markdown;
            }
            else if (DetailMarkdownEditor is not null)
            {
                DetailMarkdownEditor.ShowFormattingToolbar =
                    _detailContentFormat == TextContentFormat.Markdown;
            }
        }
    }

    private MarkdownSourceEditor EnsureDetailMarkdownEditorRealized()
    {
        if (DetailMarkdownEditor is null)
        {
            _ = FindName(nameof(DetailMarkdownEditor));
        }

        MarkdownSourceEditor editor = DetailMarkdownEditor!;
        if (!_detailMarkdownEditorWired)
        {
            _detailMarkdownEditorWired = true;
            editor.TextResolver = _localizationService.T;
            editor.EditorTextChanged += DetailMarkdownEditor_EditorTextChanged;
            editor.TextTruncated += DetailMarkdownEditor_TextTruncated;
            editor.CommitRequested += DetailMarkdownEditor_CommitRequested;
            WireDetailEditorOptimizationEvents(editor);
        }

        return editor;
    }

    private void DetailEditButton_Click(object sender, RoutedEventArgs e)
    {
        BeginDetailEditing(GetDetailReaderScrollRatio());
    }

    private void DetailBodyReaderSurface_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        double? ratio = DetailMarkdownView.Visibility == Visibility.Visible
            ? GetDetailReaderScrollRatio(e.GetPosition(DetailMarkdownView))
            : null;
        if (BeginDetailEditing(ratio))
        {
            e.Handled = true;
        }
    }

    private bool BeginDetailEditing() => BeginDetailEditing(null);

    private bool BeginDetailEditing(double? proportionalVerticalRatio)
    {
        if (_isDetailEditing ||
            (!_isCreatingDetail && _detailItem is null))
        {
            return false;
        }

        EnsureDetailMarkdownEditorRealized();
        _detailContentFormat = ViewModel.EditorContentFormat;
        SetDetailEditorText(_detailItem?.Body ?? string.Empty);
        _isDetailEditing = true;
        RefreshDetailPresentation();
        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "quick-capture-detail-edit");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed && _isDetailEditing)
            {
                MarkdownSourceEditor realizedEditor = EnsureDetailMarkdownEditorRealized();
                if (proportionalVerticalRatio is double ratio)
                {
                    realizedEditor.FocusEditorAtProportionalPosition(ratio);
                }
                else
                {
                    realizedEditor.FocusEditor(moveCaretToEnd: false);
                }
            }
        });
        return true;
    }

    private ScrollViewer? GetDetailReaderScrollViewer()
    {
        if (DetailMarkdownView?.Content is ScrollViewer directViewer)
        {
            return directViewer;
        }

        return DetailMarkdownView is null
            ? null
            : FindFirstDescendantOfType<ScrollViewer>(DetailMarkdownView);
    }

    private double GetDetailReaderScrollRatio(Windows.Foundation.Point? clickPointInReader = null)
    {
        ScrollViewer? scrollViewer = GetDetailReaderScrollViewer();
        if (scrollViewer is null)
        {
            return 0;
        }

        if (clickPointInReader is { } pt)
        {
            double extent = Math.Max(1.0, Math.Max(scrollViewer.ExtentHeight, scrollViewer.ViewportHeight));
            double contentY = Math.Max(0.0, scrollViewer.VerticalOffset + pt.Y);
            return Math.Clamp(contentY / extent, 0.0, 1.0);
        }

        if (scrollViewer.ScrollableHeight <= 0.5)
        {
            return 0;
        }

        return Math.Clamp(scrollViewer.VerticalOffset / scrollViewer.ScrollableHeight, 0.0, 1.0);
    }

    private void RestoreDetailReaderScrollRatio(double ratio)
    {
        double clamped = Math.Clamp(ratio, 0.0, 1.0);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_isDisposed || _isDetailEditing || DetailMarkdownView.Visibility != Visibility.Visible)
            {
                return;
            }

            ScrollViewer? scrollViewer = GetDetailReaderScrollViewer();
            if (scrollViewer is not null && scrollViewer.ScrollableHeight > 0.5)
            {
                _ = scrollViewer.ChangeView(
                    null,
                    clamped * scrollViewer.ScrollableHeight,
                    null,
                    disableAnimation: true);
            }
        });
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
        if (_suppressDetailEditorChanges || !_isDetailEditing)
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

        if (_detailHasUnsavedChanges || _isSavingDetail || (_isCreatingDetail && HasNewDetailContent()))
        {
            await SaveDetailAsync(completeEditing: false);
        }
    }

    private bool HasNewDetailContent() =>
        !string.IsNullOrWhiteSpace(DetailMarkdownEditor?.Text) ||
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

            double? exitEditorRatio = completeEditing && _isDetailEditing && DetailMarkdownEditor is not null
                ? DetailMarkdownEditor.GetVerticalScrollRatio()
                : null;
            if (completeEditing)
            {
                _isDetailEditing = false;
            }

            RefreshDetailAfterSave();
            if (exitEditorRatio is double readerTargetRatio)
            {
                RestoreDetailReaderScrollRatio(readerTargetRatio);
            }

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
                !string.IsNullOrWhiteSpace(DetailMarkdownEditor?.Text))
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
        string body = DetailMarkdownEditor?.Text ?? (_detailItem?.Body ?? string.Empty);
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
            long refreshSequenceBeforeWrite = ViewModel.CurrentRefreshSequence;
            if (_pendingDetailAttachments.Count > 0)
            {
                QuickCaptureItemViewModel? attached =
                    await ViewModel.AddItemWithAttachmentsAsync(_pendingDetailAttachments);
                if (attached is null)
                {
                    return false;
                }

                refreshSequenceBeforeWrite = ViewModel.CurrentRefreshSequence;
                QuickCaptureWriteResult updateResult =
                    await ViewModel.EditItemDetailsWithResultAsync(
                        attached,
                        attached.Title,
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
                await ViewModel.EnsureRefreshedSinceAsync(refreshSequenceBeforeWrite);
                _detailItem = ViewModel.Items.FirstOrDefault(item => item.Id == created.Id);
                _isCreatingDetail = false;
                _pendingDetailAttachments.Clear();
            }

            _detailSavedRevision = Math.Max(_detailSavedRevision, revisionAtStart);
            _detailHasUnsavedChanges = _detailEditRevision > revisionAtStart;
            return true;
        }

        if (_detailItem is not { } item)
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

            long refreshSequenceBeforeWrite = ViewModel.CurrentRefreshSequence;
            QuickCaptureWriteResult updateResult =
                await ViewModel.EditItemDetailsWithResultAsync(
                    item,
                    item.Title,
                    body,
                    _detailAppearance,
                    _detailContentFormat);
            if (!updateResult.Saved)
            {
                return false;
            }

            ReportBodyTruncation(updateResult);
            await ViewModel.EnsureRefreshedSinceAsync(refreshSequenceBeforeWrite);
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
            if (_isDetailEditing)
            {
                MarkdownSourceEditor editor = EnsureDetailMarkdownEditorRealized();
                if (!_detailHasUnsavedChanges &&
                    !string.Equals(editor.Text, refreshed.Body, StringComparison.Ordinal))
                {
                    SetDetailEditorText(refreshed.Body);
                }
            }

            DetailMarkdownView.Markdown = _isDetailEditing
                ? (DetailMarkdownEditor?.Text ?? refreshed.Body)
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
        string normalized = value ?? string.Empty;
        if (DetailMarkdownEditor is null && normalized.Length == 0)
        {
            return;
        }

        MarkdownSourceEditor editor = EnsureDetailMarkdownEditorRealized();
        _suppressDetailEditorChanges = true;
        try
        {
            editor.Text = normalized;
        }
        finally
        {
            _suppressDetailEditorChanges = false;
        }
    }

    private async void DetailBackButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateBackFromDetailAsync();
    }

    private async Task NavigateBackFromDetailAsync()
    {
        bool wasShowingSinglePaneDetail = !_isDualPane && _showDetailInSinglePane;
        if (_isCreatingDetail && !HasNewDetailContent())
        {
            ClearDetailForViewChange();
            if (wasShowingSinglePaneDetail)
            {
                PlaySinglePaneNavigationTransition(enteringDetail: false);
            }
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
        if (!_isDualPane)
        {
            _detailImageLoadVersion++;
            _detailPrimaryImagePath = null;
            DetailPrimaryImage.Source = null;
            SetDetailPrimaryImageEstimatedBytes(0);
        }

        ApplyResponsiveLayout();
        RefreshDetailPresentation();
        if (wasShowingSinglePaneDetail)
        {
            PlaySinglePaneNavigationTransition(enteringDetail: false);
        }
    }

    private async void DetailPinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is { } item)
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

        ApplyMaterialPaletteSwatches();
        DetailMaterialSurface.Background = ResolveMaterialBrush(_detailAppearance);
        foreach (Button button in GetDetailMaterialButtons())
        {
            bool selected = string.Equals(
                button.Tag as string,
                _detailAppearance.ToString(),
                StringComparison.Ordinal);
            // A selected swatch ring is a selection state, so it uses the
            // neutral strong stroke rather than the accent.
            button.BorderBrush = selected
                ? new SolidColorBrush(NeutralInteractionBrush.Line(button))
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
        if (_detailItem is not { } item)
        {
            return;
        }

        await DeleteQuickCaptureItemAsync(item);
    }

    private async void DetailMarkdownView_TaskToggleRequested(
        object? sender,
        MarkdownTaskToggleRequestedEventArgs e)
    {
        if (_detailContentFormat != TextContentFormat.Markdown ||
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
        if (!_isDetailEditing)
        {
            return;
        }

        try
        {
            IntPtr foreground = Win32Helper.GetForegroundWindow();
            IntPtr owner = Win32Helper.GetAncestor(foreground, Win32Helper.GA_ROOT);
            IReadOnlyList<string> files =
                await FileOpenPickerService.PickMultipleFilesAsync(
                    owner == IntPtr.Zero ? foreground : owner,
                    [],
                    PickerLocationId.Desktop);
            DroppedFilePath[] paths = files
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(path => new DroppedFilePath(
                    path,
                    Path.GetFileName(path),
                    ForceManagedCopy: false))
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
        try
        {
            TodoAttachmentViewModel attachment = e.Attachment;
            if (!File.Exists(attachment.FilePath))
            {
                return;
            }

            bool launched = false;
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
                launched = await Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch (Exception launchEx)
            {
                App.LogVerbose($"[QuickCapture] Launcher.LaunchFileAsync declined '{attachment.FilePath}': {launchEx.Message}");
            }

            if (!launched)
            {
                DeskBox.Platform.Win32Helper.OpenFileOrChooseApp(IntPtr.Zero, attachment.FilePath);
            }
        }
        catch (Exception ex)
        {
            // Deep review R17 (G3 audit): async void without a catch kills
            // the process on launcher failures — match the sibling handlers.
            App.Log($"[QuickCapture] Attachment open failed: {ex.Message}");
        }
    }

    private async void DetailAttachmentStrip_RemoveRequested(
        object? sender,
        AttachmentTileEventArgs e)
    {
        if (!_isDetailEditing)
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
            (DetailMarkdownEditor is null
                ? string.IsNullOrWhiteSpace(GetDetailPresentationBody())
                : string.IsNullOrWhiteSpace(DetailMarkdownEditor.Text)))
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
            return DetailMarkdownEditor?.Text ?? (_detailItem?.Body ?? string.Empty);
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
        DetailAttachmentStrip.CanRemove = _isDetailEditing;
        DetailAttachmentStrip.Visibility = attachments.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        bool showPrimaryImage = _detailItem?.Type == QuickCaptureItemType.Image &&
                                _detailItem.SourceKind is QuickCaptureSourceKind.Clipboard or QuickCaptureSourceKind.Image;
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
        SetDetailPrimaryImageEstimatedBytes(0);

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
            PerformanceLogger.RecordQuickCaptureDetailImageDecode();
            SetDetailPrimaryImageEstimatedBytes(
                (long)DetailImageDecodePixelWidth *
                DetailImageDecodePixelWidth *
                4);
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

    private void SetDetailPrimaryImageEstimatedBytes(long estimatedBytes)
    {
        long normalizedBytes = Math.Max(0, estimatedBytes);
        long delta = normalizedBytes - _detailPrimaryImageEstimatedBytes;
        _detailPrimaryImageEstimatedBytes = normalizedBytes;
        if (delta != 0)
        {
            PerformanceLogger.AdjustQuickCaptureDetailImageEstimatedBytes(
                delta);
        }
    }

    private string? ResolveDetailAttachmentPath(string attachmentId) =>
        _detailItem?.Attachments.FirstOrDefault(attachment =>
            string.Equals(attachment.Id, attachmentId, StringComparison.Ordinal))?.FilePath;

    private async void DetailMarkdownView_AttachmentOpenRequested(
        object? sender,
        MarkdownAttachmentRequestedEventArgs e)
    {
        try
        {
            string? path = ResolveDetailAttachmentPath(e.AttachmentId);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                bool launched = false;
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                    launched = await Windows.System.Launcher.LaunchFileAsync(file);
                }
                catch (Exception launchEx)
                {
                    App.LogVerbose($"[QuickCapture] Markdown Launcher.LaunchFileAsync declined '{path}': {launchEx.Message}");
                }

                if (!launched)
                {
                    DeskBox.Platform.Win32Helper.OpenFileOrChooseApp(IntPtr.Zero, path);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Markdown attachment open failed: {ex.Message}");
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
        bool willPin = !item.IsPinned;
        bool changed = false;
        await RunAsync(async () =>
        {
            changed = await ViewModel.TogglePinnedAsync(item);
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

    private void PopoutItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickCaptureItemViewModel item })
        {
            App.Current?.OpenQuickCapturePopoutWindow(
                item.Id,
                startAlwaysOnTop: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift));
        }
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

        IReadOnlyList<QuickCaptureItemViewModel> targetItems = ResolveRightTappedTargetItems(item);
        MenuFlyout flyout = CreateRichItemOrBatchContextFlyout(targetItems, primaryItem: item);
        flyout.ShowAt(
            anchor,
            new FlyoutShowOptions
            {
                Position = e.GetPosition(anchor),
                ShowMode = FlyoutShowMode.Standard
            });
        e.Handled = true;
    }

    private void QuickCaptureItem_RightTappedLegacy_Unused(
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

        var copyItem = CreateContextMenuItem("Common.Copy", "\uE8C8");
        copyItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await CopyItemWithFeedbackAsync(item);
        };
        flyout.Items.Add(copyItem);
        flyout.Items.Add(CreateAppearanceContextSubmenu(item, flyout));

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
        ScheduleDeletedSnapshotsAttachmentRelease([deletedSnapshot]);
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
        if (isLeftButtonPressed && !IsInteractiveQuickCaptureSource(e.OriginalSource))
        {
            DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "quick-capture-list-card");
            UpdateOwnerFocusSelectionVisuals();
        }

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
        // Export drags are copy-only: advertising Move would let a Win10
        // Explorer drop physically move the backing attachment file out of
        // quick-capture storage, and multi-effect offers prompt the picker.
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void ItemsList_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        ResetInternalQuickCaptureDrag();
    }

    private void QuickCaptureTab_DragOver(object sender, DragEventArgs e)
    {
        if (!_isInternalQuickCaptureDrag)
        {
            if (sender is FrameworkElement { Tag: string extTag } &&
                TryGetQuickCaptureTabTarget(extTag, out _) &&
                DeskBoxDragData.TryGetTodoDragItems(e.DataView) is { Count: > 0 })
            {
                e.Handled = true;
                e.AcceptedOperation = e.AllowedOperations.HasFlag(DataPackageOperation.Copy)
                    ? DataPackageOperation.Copy
                    : DeskBoxDragData.ResolveInternalMetadataOperation(e.AllowedOperations);
                e.DragUIOverride.IsGlyphVisible = true;
            }
            return;
        }

        if (sender is not FrameworkElement { Tag: string tag } ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            !ViewModel.CanApplyTabDrop(GetDraggedQuickCaptureItems(), target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        e.AcceptedOperation =
            DeskBoxDragData.ResolveInternalMetadataOperation(
                e.AllowedOperations);
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void QuickCaptureTab_Drop(object sender, DragEventArgs e)
    {
        if (!_isInternalQuickCaptureDrag)
        {
            if (sender is FrameworkElement { Tag: string extTag } &&
                TryGetQuickCaptureTabTarget(extTag, out QuickCaptureViewMode extTarget) &&
                DeskBoxDragData.TryGetTodoDragItems(e.DataView) is { Count: > 0 } todoItems)
            {
                e.Handled = true;
                var extDeferral = e.GetDeferral();
                try
                {
                    int imported = await ViewModel.ImportTodoItemsAsync(
                        todoItems,
                        forcePin: extTarget == QuickCaptureViewMode.Pinned);
                    e.AcceptedOperation = imported > 0
                        ? (e.AllowedOperations.HasFlag(DataPackageOperation.Copy)
                            ? DataPackageOperation.Copy
                            : DeskBoxDragData.ResolveInternalMetadataOperation(e.AllowedOperations))
                        : DataPackageOperation.None;
                    if (imported > 0)
                    {
                        ViewModel.SelectedView = extTarget;
                        UpdateSelectedViewVisual();
                        RaiseFeedback(
                            T("QuickCapture.Dropped"),
                            WidgetFeedbackSeverity.Success,
                            "quick-todo-drop");
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[QuickCapture] External tab drop failed: {ex.Message}");
                    e.AcceptedOperation = DataPackageOperation.None;
                }
                finally
                {
                    extDeferral.Complete();
                }
            }
            return;
        }

        IReadOnlyList<QuickCaptureItemViewModel> draggedItems =
            GetDraggedQuickCaptureItems();
        if (sender is not FrameworkElement { Tag: string tag } ||
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
        catch (Exception ex)
        {
            // Deep review R17 (G3 audit): async void drop handler — an
            // ApplyTabDropAsync throw rode straight to the dispatcher.
            App.Log($"[QuickCapture] Tab drop failed: {ex.Message}");
            e.AcceptedOperation = DataPackageOperation.None;
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
        switch (tag)
        {
            case "Pinned":
                target = QuickCaptureViewMode.Pinned;
                return true;
            case "Records":
                target = QuickCaptureViewMode.Records;
                return true;
            default:
                target = QuickCaptureViewMode.Records;
                return false;
        }
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

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOwnerFocusSelectionVisuals();
    }

    private async void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        bool controlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        if (!controlPressed &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu) &&
            e.Key == Windows.System.VirtualKey.Enter &&
            ItemsList.SelectedItem is QuickCaptureItemViewModel enterItem)
        {
            e.Handled = true;
            await OpenDetailAfterSavingAsync(enterItem);
            return;
        }

        if (controlPressed && e.Key == Windows.System.VirtualKey.A)
        {
            ItemsList.SelectAll();
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Windows.System.VirtualKey.V)
        {
            e.Handled = true;
            await TryPasteIntoListAsync();
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
                selectedItems.Select(item => item.Id));
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
            ScheduleDeletedSnapshotsAttachmentRelease(deletedItems);
        }
    }

    private void ScheduleDeletedSnapshotsAttachmentRelease(
        IReadOnlyList<QuickCaptureDeletedItemSnapshot> snapshots)
    {
        IReadOnlyList<QuickCaptureDeletedItemSnapshot> superseded = _pendingCleanupSnapshots;
        CancellationTokenSource? oldCts = _pendingSnapshotCleanupCts;
        _pendingCleanupSnapshots = snapshots;
        var cts = new CancellationTokenSource();
        _pendingSnapshotCleanupCts = cts;

        if (oldCts is not null)
        {
            try
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            catch
            {
            }
        }

        QuickCaptureWidgetViewModel vm = ViewModel;
        if (superseded.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await vm.ReleaseDeletedItemSnapshotsAsync(superseded);
                }
                catch (Exception ex)
                {
                    App.Log($"[QuickCapture] Superseded snapshot attachment cleanup failed: {ex.Message}");
                }
            });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(QuickCaptureService.UndoAttachmentRetentionWindow, cts.Token);
                if (ReferenceEquals(_pendingSnapshotCleanupCts, cts))
                {
                    _pendingCleanupSnapshots = [];
                }

                await vm.ReleaseDeletedItemSnapshotsAsync(snapshots);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Log($"[QuickCapture] Post-undo attachment cleanup failed: {ex.Message}");
            }
        });
    }

    private void CancelPendingSnapshotCleanupWithoutDeleting()
    {
        _pendingCleanupSnapshots = [];
        if (_pendingSnapshotCleanupCts is { } cts)
        {
            _pendingSnapshotCleanupCts = null;
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
            }
        }
    }

    internal static bool CanImportExternalDrop(DataPackageView? dataView)
    {
        if (dataView is null)
        {
            return false;
        }

        return DeskBoxDragData.TryGetTodoDragItems(dataView) is { Count: > 0 } ||
               DeskBoxDragData.HasDroppedFiles(dataView) ||
               dataView.Contains(DeskBoxDragData.TextFormat) ||
               dataView.Contains(StandardDataFormats.Text) ||
               dataView.Contains(StandardDataFormats.WebLink);
    }

    internal async Task<bool> ImportExternalDropAsync(DataPackageView? dataView)
    {
        if (dataView is null || _isDisposed)
        {
            return false;
        }

        DataPackageOperation op = await ImportExternalDropCoreAsync(
            dataView,
            DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link);
        return op != DataPackageOperation.None;
    }

    private async Task<DataPackageOperation> ImportExternalDropCoreAsync(
        DataPackageView dataView,
        DataPackageOperation allowedOperations)
    {
        if (DeskBoxDragData.TryGetTodoDragItems(dataView) is { Count: > 0 } todoItems)
        {
            int imported = await ViewModel.ImportTodoItemsAsync(todoItems);
            if (imported <= 0)
            {
                return DataPackageOperation.None;
            }

            RaiseFeedback(
                T("QuickCapture.Dropped"),
                WidgetFeedbackSeverity.Success,
                "quick-todo-drop");
            return allowedOperations.HasFlag(DataPackageOperation.Copy)
                ? DataPackageOperation.Copy
                : DeskBoxDragData.ResolveInternalMetadataOperation(allowedOperations);
        }

        if (DeskBoxDragData.HasDroppedFiles(dataView))
        {
            using DroppedFileBatch batch =
                await DeskBoxDragData.TryGetDroppedFilesAsync(dataView);
            QuickCaptureItemViewModel? created =
                await ViewModel.AddItemWithAttachmentsAsync(batch.Files);
            if (created is null)
            {
                return DataPackageOperation.None;
            }

            RaiseFeedback(
                T("QuickCapture.Dropped"),
                WidgetFeedbackSeverity.Success,
                "quick-drop");
            return DeskBoxDragData.GetFileAssociationOperation(dataView, allowedOperations);
        }

        string? text = await DeskBoxDragData.TryGetTextAsync(dataView);
        if (!string.IsNullOrWhiteSpace(text))
        {
            QuickCaptureWriteResult result = await ViewModel.AddTextAsync(text);
            ReportBodyTruncation(result);
            if (result.Saved)
            {
                RaiseFeedback(
                    T("QuickCapture.Dropped"),
                    WidgetFeedbackSeverity.Success,
                    "quick-drop");
                return DataPackageOperation.Copy;
            }
        }

        return DataPackageOperation.None;
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

        if (!CanImportExternalDrop(e.DataView))
        {
            return;
        }

        if (DeskBoxDragData.TryGetTodoDragItems(e.DataView) is { Count: > 0 })
        {
            e.AcceptedOperation = e.AllowedOperations.HasFlag(DataPackageOperation.Copy)
                ? DataPackageOperation.Copy
                : DeskBoxDragData.ResolveInternalMetadataOperation(e.AllowedOperations);
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.Caption = T("Widget.Compact.QuickCaptureDropHint");
            e.Handled = true;
            return;
        }

        bool hasFiles = DeskBoxDragData.HasDroppedFiles(e.DataView);
        e.AcceptedOperation = hasFiles
            ? DeskBoxDragData.GetFileAssociationOperation(
                e.DataView,
                e.AllowedOperations)
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

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            e.AcceptedOperation = await ImportExternalDropCoreAsync(
                e.DataView,
                e.AllowedOperations);
        }
        catch (Exception ex)
        {
            e.AcceptedOperation = DataPackageOperation.None;
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
            e.AcceptedOperation =
                DeskBoxDragData.ResolveInternalMetadataOperation(
                    e.AllowedOperations);
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
            DeskBoxDragData.GetFileAssociationOperation(
                e.DataView,
                e.AllowedOperations);
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
                T("Common.OperationFailedRetry"),
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
        // Insertion position is a drag state, so it draws the neutral
        // interaction line rather than the accent.
        border.BorderBrush = active
            ? new SolidColorBrush(NeutralInteractionBrush.Line(border))
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
        // As in the file surface, a drop target reads as the neutral hover
        // surface: neutral wash plus a neutral border, with no accent and no
        // hardcoded fallback colour.
        border.Background = active
            ? new SolidColorBrush(NeutralInteractionBrush.Fill(border))
            : new SolidColorBrush(Colors.Transparent);
        border.BorderBrush = active
            ? new SolidColorBrush(NeutralInteractionBrush.Line(border))
            : new SolidColorBrush(Colors.Transparent);
        border.BorderThickness = new Thickness(active ? 1 : 0);
    }

    private Brush ResolveBrush(string key, Color fallback)
    {
        // A bare Application.Resources lookup resolves theme dictionaries
        // against the application theme (the system theme captured at
        // startup), so it kept returning the light card brush after the
        // widget was overridden to dark — the light veil over the list and
        // detail surfaces. Resolve by this element's own theme instead.
        return NeutralInteractionBrush.ResolveThemedResource(key, this) ??
               new SolidColorBrush(fallback);
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

    private void ItemsList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            QuickCaptureItemViewModel? recycledItem =
                args.Item as QuickCaptureItemViewModel ??
                (args.ItemContainer?.ContentTemplateRoot as FrameworkElement)?.DataContext as QuickCaptureItemViewModel;
            if (recycledItem is not null &&
                !((_isDualPane || _showDetailInSinglePane) &&
                  string.Equals(recycledItem.Id, _detailItem?.Id, StringComparison.Ordinal)))
            {
                recycledItem.ReleaseAttachmentThumbnails();
            }

            return;
        }

        if (args.Item is QuickCaptureItemViewModel liveItem &&
            liveItem.PrimaryImageAttachment is { } primaryAttachment)
        {
            _ = EnsureQuickCaptureAttachmentThumbnailAsync(primaryAttachment);
        }
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
        if (itemRoot is null)
        {
            return;
        }

        if (FindQuickCaptureVisualChild<Border>(itemRoot, "QuickCaptureItemActionButtons") is { } actionButtons)
        {
            actionButtons.Opacity = isVisible ? 1 : 0;
            actionButtons.IsHitTestVisible = isVisible;
        }

        // Same pass also toggles the hover wash so the card itself reacts,
        // matching the Todo widget's dual hover feedback.
        if (FindQuickCaptureVisualChild<Border>(itemRoot, "QuickCaptureItemHoverBackground") is { } hoverBackground)
        {
            hoverBackground.Opacity = isVisible ? 1 : 0;
        }
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
        RebuildFilterChipBar();
        // The segmented pointer states copy theme-dependent neutral colors at
        // apply time, so a light/dark flip must re-apply them (parity with the
        // standalone window's OnRootElementThemeChanged).
        ApplySegmentedStyle();
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
            ApplyQuickCaptureItemCardPresentation(border, null);
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
        ApplyQuickCaptureItemCardPresentation(border, item);
        border.Background = ResolveMaterialBrush(preset);
        ApplyItemSelectionFocusVisual(border, HasOwnerKeyboardFocus());
    }

    private Brush ResolveMaterialBrush(QuickCaptureAppearancePreset preset)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        if (QuickCaptureMaterialBrushCache.GetSurfaceBrush(preset, dark) is { } cachedSurfaceBrush)
        {
            return cachedSurfaceBrush;
        }
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

    private Func<Task> CreateSingleExecutionUndoAction(Func<Task> inner)
    {
        int invoked = 0;
        Func<Task>? wrapper = null;
        wrapper = async () =>
        {
            if (Interlocked.Exchange(ref invoked, 1) != 0)
            {
                return;
            }

            if (ReferenceEquals(_lastUndoAction, wrapper))
            {
                _lastUndoAction = null;
            }

            CancelPendingSnapshotCleanupWithoutDeleting();
            await RunAsync(inner);
        };
        return wrapper;
    }

    private void RaiseFeedback(
        string message,
        WidgetFeedbackSeverity severity,
        string deduplicationKey,
        string? actionText = null,
        Func<Task>? action = null)
    {
        Func<Task>? wrappedAction = action is null
            ? null
            : CreateSingleExecutionUndoAction(action);
        if (wrappedAction is not null)
        {
            _lastUndoAction = wrappedAction;
        }

        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(
                new WidgetFeedbackRequest(
                    message,
                    severity,
                    deduplicationKey,
                    actionText,
                    wrappedAction)));
    }

    private string T(string key) =>
        _localizationService.T(key);

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        DisposeLeafOnly();
        ViewModel.Dispose();
    }

    private DoubleTappedEventHandler? _detailBodyReaderDoubleTappedHandler;

    internal sealed record QuickCaptureSurfaceSwitchState(
        QuickCaptureExtendedTransientState ExtendedState,
        bool IsInlineComposerOpen = false,
        string? InlineComposerDraft = null,
        DroppedFilePath[]? PendingInlineAttachments = null);

    internal sealed record QuickCaptureHibernatingWork(
        bool IsCreatingDetail,
        QuickCaptureItemViewModel? ExistingItem,
        string Body,
        QuickCaptureAppearancePreset Appearance,
        TextContentFormat ContentFormat,
        DroppedFilePath[] PendingAttachments,
        string? InlineComposerDraft = null,
        DroppedFilePath[]? PendingInlineAttachments = null);

    private void ReleaseDetailStripAttachmentThumbnails()
    {
        if (DetailAttachmentStrip?.ItemsSource is System.Collections.IEnumerable items)
        {
            foreach (object? entry in items)
            {
                if (entry is TodoAttachmentViewModel attachment)
                {
                    attachment.ReleaseThumbnail();
                }
            }
        }
    }

    private void ReleaseInlineComposerStripAttachmentThumbnails()
    {
        if (InlineComposerAttachmentStrip?.ItemsSource is System.Collections.IEnumerable items)
        {
            foreach (object? entry in items)
            {
                if (entry is TodoAttachmentViewModel attachment)
                {
                    attachment.ReleaseThumbnail();
                }
            }
        }
    }

    internal QuickCaptureHibernatingWork? FlushPendingEditsForShutdown()
    {
        if (_isDisposed)
        {
            return null;
        }

        _detailAutoSaveTimer?.Stop();
        string? inlineDraft = InlineComposerTextBox?.Text?.Trim();
        DroppedFilePath[] inlineAttachments = _pendingInlineAttachments.ToArray();
        bool hasInlineWork = !string.IsNullOrWhiteSpace(inlineDraft) || inlineAttachments.Length > 0;
        if (hasInlineWork)
        {
            if (InlineComposerTextBox is not null)
            {
                InlineComposerTextBox.Text = string.Empty;
            }

            _pendingInlineAttachments.Clear();
            _isInlineComposerOpen = false;
        }

        if (_isCreatingDetail)
        {
            string body = DetailMarkdownEditor?.Text ?? string.Empty;
            DroppedFilePath[] attachments = _pendingDetailAttachments.ToArray();
            bool hasDetailWork = !string.IsNullOrWhiteSpace(body) || attachments.Length > 0;
            if (hasDetailWork)
            {
                _pendingDetailAttachments.Clear();
                _isCreatingDetail = false;
                _detailHasUnsavedChanges = false;
                return new QuickCaptureHibernatingWork(
                    true,
                    null,
                    body,
                    _detailAppearance,
                    _detailContentFormat,
                    attachments,
                    hasInlineWork ? inlineDraft : null,
                    hasInlineWork ? inlineAttachments : null);
            }
        }
        else if (_detailItem is { } existing)
        {
            string body = DetailMarkdownEditor?.Text ?? string.Empty;
            bool detailsChanged =
                _detailHasUnsavedChanges ||
                _detailAppearance != existing.AppearancePreset ||
                _detailContentFormat != existing.ContentFormat;
            if (_isDetailEditing && detailsChanged && !string.IsNullOrWhiteSpace(body))
            {
                _detailHasUnsavedChanges = false;
                return new QuickCaptureHibernatingWork(
                    false,
                    existing,
                    body,
                    _detailAppearance,
                    _detailContentFormat,
                    [],
                    hasInlineWork ? inlineDraft : null,
                    hasInlineWork ? inlineAttachments : null);
            }
        }

        return hasInlineWork
            ? new QuickCaptureHibernatingWork(
                false,
                null,
                string.Empty,
                QuickCaptureAppearancePreset.Default,
                ViewModel.EditorContentFormat,
                [],
                inlineDraft,
                inlineAttachments)
            : null;
    }

    internal QuickCaptureHibernatingWork? ExtractAndReleasePendingWorkBeforeTeardown()
    {
        if (_isDisposed)
        {
            return null;
        }

        _detailAutoSaveTimer?.Stop();
        ReleaseDetailStripAttachmentThumbnails();
        ReleaseInlineComposerStripAttachmentThumbnails();
        return FlushPendingEditsForShutdown();
    }

    internal void PromoteHibernatedDraftToPersistedItem(string createdItemId)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(createdItemId))
        {
            return;
        }

        if (_pendingCreatingDetail)
        {
            _pendingCreatingDetail = false;
            _pendingDetailItemId = createdItemId;
            _pendingDetailEditing = true;
            _pendingRestoredDetailAttachments = [];
            return;
        }

        if (_isCreatingDetail)
        {
            QuickCaptureItemViewModel? createdVm = ViewModel.Items.FirstOrDefault(item =>
                string.Equals(item.Id, createdItemId, StringComparison.Ordinal));
            if (createdVm is not null)
            {
                _isCreatingDetail = false;
                _detailItem = createdVm;
                _pendingDetailAttachments.Clear();
                DetailPinIcon.IsPinned = createdVm.IsPinned;
                foreach (QuickCaptureItemViewModel candidate in ViewModel.Items)
                {
                    candidate.IsDetailSelected = candidate.Id == createdVm.Id;
                }
                RefreshDetailAttachments();
                RefreshDetailPresentation();
            }
        }
    }

    internal void DisposeLeafOnly()
    {
        if (_isDisposed)
        {
            return;
        }

        ReleaseDetailStripAttachmentThumbnails();
        _pendingDetailAttachments.Clear();
        _isDisposed = true;
        _detailImageLoadVersion++;
        DetailPrimaryImage.Source = null;
        SetDetailPrimaryImageEstimatedBytes(0);
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
        if (_detailBodyReaderDoubleTappedHandler is not null)
        {
            DetailBodyReaderSurface.RemoveHandler(
                UIElement.DoubleTappedEvent,
                _detailBodyReaderDoubleTappedHandler);
            _detailBodyReaderDoubleTappedHandler = null;
        }
        if (_surfaceKeyDownHandler is not null)
        {
            ResponsiveContentGrid.RemoveHandler(
                UIElement.KeyDownEvent,
                _surfaceKeyDownHandler);
            _surfaceKeyDownHandler = null;
        }
        if (_surfacePointerPressedHandler is not null)
        {
            ResponsiveContentGrid.RemoveHandler(
                UIElement.PointerPressedEvent,
                _surfacePointerPressedHandler);
            _surfacePointerPressedHandler = null;
        }
        if (DetailMarkdownEditor is not null)
        {
            if (_detailMarkdownEditorWired)
            {
                _detailMarkdownEditorWired = false;
                DetailMarkdownEditor.EditorTextChanged -= DetailMarkdownEditor_EditorTextChanged;
                DetailMarkdownEditor.TextTruncated -= DetailMarkdownEditor_TextTruncated;
                DetailMarkdownEditor.CommitRequested -= DetailMarkdownEditor_CommitRequested;
                UnwireDetailEditorOptimizationEvents(DetailMarkdownEditor);
            }
            DetailMarkdownEditor.ReleaseVisualHandlers();
        }
        DetailMarkdownView.AttachmentOpenRequested -= DetailMarkdownView_AttachmentOpenRequested;
        DetailMarkdownView.AttachmentResolver = null;
        DetailMarkdownView.ReleaseVisualHandlers();
        DisposeOptimizationResources();
        DetailAttachmentStrip.ReleaseVisualHandlers();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ItemsList.ItemsSource = null;
        // QuickCaptureSurfaceContent.xaml only uses {x:Bind} inside DataTemplates, so
        // the generated root Bindings field stays null unless root {x:Bind} is added.
        Bindings?.StopTracking();
        ResponsiveContentGrid.DataContext = null;
        Content = null;
    }
}
