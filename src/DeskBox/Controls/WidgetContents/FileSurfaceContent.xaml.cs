using System.Collections.Specialized;
using DeskBox.Controls;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Shared file-widget content used by both standalone and grouped unified hosts.
/// </summary>
public sealed partial class FileSurfaceContent :
    UserControl,
    IWidgetContent,
    ICancellableWidgetContent,
    IWidgetGroupContentCacheable,
    IWidgetAddActionContent,
    IWidgetFeedbackSource,
    IWidgetHostContextMenuSource,
    IWidgetTransientStateContent,
    IDisposable
{
    private const int StackDuplicateInputWindowMs = 120;
    private readonly LocalizationService _localizationService;
    private readonly FileService _fileService;
    private readonly SettingsService _settingsService;
    private readonly StackInputActivationArbiter _stackInputActivation = new();
    private static readonly QuickLookPreviewService s_quickLookService =
        new();
    private string[] _cutClipboardPaths = [];
    private WidgetItem? _itemRenameTarget;
    private string? _itemRenameStackKey;
    private TextBlock? _itemRenameNameText;
    private bool _isCommittingItemRename;
    private bool _isCancellingItemRename;
    private bool _isSurfaceReorderDragActive;
    private string[] _surfaceReorderPaths = [];
    private string? _surfaceReorderStackKey;
    private int _surfaceReorderInsertionIndex = -1;
    private Windows.Foundation.Point _surfaceReorderLastPosition;
    private bool _surfaceReorderHasLastPosition;
    private WidgetItem? _surfaceReorderDraggedItem;
    private HashSet<string>? _surfaceReorderPathSet;
    private ListViewBase? _surfaceReorderLastView;
    private WidgetItem[] _pendingPointerDragItems = [];
    private string[] _activeDragSourcePaths = [];
    private bool _activeDragHasStorageItems;
    private bool _activeDragHandledAsStackMembership;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Border? _folderDropTarget;
    private Border? _stackMemberDropTarget;
    private WidgetStackItem? _pressedStack;
    private bool _stackPointerDragStarted;
    private string? _lastStackInputKey;
    private long _lastStackInputTick;
    private bool _isImportBusy;
    private IntPtr _hostWindowHandle;
    private DateTimeOffset? _importBusyStartedAtUtc;
    private bool _isDisposed;
    private bool _isReadyForReuse;
    private bool _hasBeenWindowVisible;
    private bool _isWindowVisible;
    private bool _isWindowRevealCompleted;
    private DateTime _lastDiskReconciliationUtc = DateTime.MinValue;
    private int _diskReconciliationQueued;
    private TransitionCollection? _suspendedGridItemContainerTransitions;
    private TransitionCollection? _suspendedListItemContainerTransitions;
    private bool _itemContainerTransitionsSuspendedForHostSwitch;
    private DragPayloadSnapshot? _dragPayloadSnapshot;
    private readonly Dictionary<string, bool> _dragDirectoryCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _dragUnsafeDropCache =
        new(StringComparer.OrdinalIgnoreCase);
    private FileDropVisualState? _lastDropVisualState;
    private bool _dragPayloadSessionActive;

    private sealed class DragPayloadSnapshot
    {
        public DragPayloadSnapshot(
            DataPackageView dataView,
            string[] paths,
            bool isInternalReorder,
            bool hasSurfacePathData,
            string? stackReorderKey,
            string? sourceStackKey,
            string? sourceWidgetId,
            string? internalDragToken)
        {
            DataView = dataView;
            Paths = paths;
            IsInternalReorder = isInternalReorder;
            HasSurfacePathData = hasSurfacePathData;
            StackReorderKey = stackReorderKey;
            SourceStackKey = sourceStackKey;
            SourceWidgetId = sourceWidgetId;
            InternalDragToken = internalDragToken;
        }

        public DataPackageView DataView { get; }

        public string[] Paths { get; }

        public bool IsInternalReorder { get; }

        public bool IsDeskBoxFileDrag =>
            Paths.Length > 0 &&
            string.Equals(
                InternalDragToken,
                DeskBoxDragData.InternalFileDragToken,
                StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(SourceWidgetId);

        public bool HasSurfacePathData { get; }

        public string? StackReorderKey { get; }

        public string? SourceStackKey { get; }

        public bool IsStackPopoverMemberDrag =>
            IsInternalReorder &&
            !string.IsNullOrWhiteSpace(SourceStackKey) &&
            Paths.Length > 0;

        public string? SourceWidgetId { get; }

        public string? InternalDragToken { get; }
    }

    public FileSurfaceContent(
        WidgetConfig config,
        FileService fileService,
        OrganizerService organizerService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue)
    {
        _fileService = fileService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        ViewModel = new WidgetViewModel(
            config,
            fileService,
            organizerService,
            settingsService,
            localizationService,
            dispatcherQueue);

        InitializeComponent();
        Root.AddHandler(
            UIElement.DragOverEvent,
            new DragEventHandler(Root_ObserveHandledDragOver),
            handledEventsToo: true);
        Root.AddHandler(
            UIElement.DragLeaveEvent,
            new DragEventHandler(Root_ObserveHandledDragLeave),
            handledEventsToo: true);
        ItemsGrid.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(ItemsView_PreviewKeyDown),
            handledEventsToo: true);
        ItemsList.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(ItemsView_PreviewKeyDown),
            handledEventsToo: true);
        RegisterScrollBarActivityTracking(ItemsGrid);
        RegisterScrollBarActivityTracking(ItemsList);
        Root.DataContext = ViewModel;
        Root.IsTabStop = true;
        EmptyAddButtonText.Text = T("Widget.AddFile");
        OpenSelectionButton.Label = T("Common.Open");
        CopySelectionButton.Label = T("Common.Copy");
        CutSelectionButton.Label = T("Common.Cut");
        DeleteSelectionButton.Label = T("Widget.MoveToRecycleBin");
        RenameSelectionButton.Label = T("Common.Rename");
        ToolTipService.SetToolTip(OpenSelectionButton, OpenSelectionButton.Label);
        ToolTipService.SetToolTip(CopySelectionButton, CopySelectionButton.Label);
        ToolTipService.SetToolTip(CutSelectionButton, CutSelectionButton.Label);
        ToolTipService.SetToolTip(DeleteSelectionButton, DeleteSelectionButton.Label);
        ToolTipService.SetToolTip(RenameSelectionButton, RenameSelectionButton.Label);
        InitializeFolderNavigationPresentation();
        ViewModel.Items.CollectionChanged += Items_CollectionChanged;
        _fileService.TransferSessions.StateChanged +=
            TransferSessions_StateChanged;
        InitializeStackPopoverLifecycle();
        ActualThemeChanged += FileSurfaceContent_ActualThemeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateEmptyState();
    }

    public WidgetViewModel ViewModel { get; }

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public event EventHandler<WidgetHostContextMenuOpeningEventArgs>?
        HostContextMenuOpening;

    internal event Action<bool>? ImportBusyChanged;

    internal bool IsImportBusy => _isImportBusy;

    internal long? ImportBusyElapsedMilliseconds =>
        _isImportBusy && _importBusyStartedAtUtc is { } startedAt
            ? Math.Max(
                0,
                (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds)
            : null;

    internal void SetHostWindowHandle(IntPtr windowHandle)
    {
        _hostWindowHandle = windowHandle;
    }

    internal void SuspendItemContainerTransitionsForHostSwitch()
    {
        if (_itemContainerTransitionsSuspendedForHostSwitch)
        {
            return;
        }

        _suspendedGridItemContainerTransitions =
            ItemsGrid.ItemContainerTransitions;
        _suspendedListItemContainerTransitions =
            ItemsList.ItemContainerTransitions;
        ItemsGrid.ItemContainerTransitions = null;
        ItemsList.ItemContainerTransitions = null;
        _itemContainerTransitionsSuspendedForHostSwitch = true;
    }

    internal void ResumeItemContainerTransitionsAfterHostSwitch()
    {
        if (!_itemContainerTransitionsSuspendedForHostSwitch)
        {
            return;
        }

        ItemsGrid.ItemContainerTransitions =
            _suspendedGridItemContainerTransitions;
        ItemsList.ItemContainerTransitions =
            _suspendedListItemContainerTransitions;
        _suspendedGridItemContainerTransitions = null;
        _suspendedListItemContainerTransitions = null;
        _itemContainerTransitionsSuspendedForHostSwitch = false;
    }

    public WidgetConfig Config => ViewModel.Config;

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => WidgetKind.File;

    public FrameworkElement View => this;

    public bool IsReadyForReuse => _isReadyForReuse && !_isDisposed;

    public Task InitializeAsync()
    {
        return InitializeAsync(CancellationToken.None);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ViewModel.InitializeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _isReadyForReuse = true;
        _lastDiskReconciliationUtc = DateTime.UtcNow;
        UpdateEmptyState();
    }

    public async Task RefreshAsync()
    {
        await ViewModel.RefreshFolderContentsAsync();
        _lastDiskReconciliationUtc = DateTime.UtcNow;
        UpdateEmptyState();
    }

    internal void RevealSavedItem(string itemPath)
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

        WidgetItem? item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Path,
                itemPath,
                StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        ListViewBase activeView = GetActiveItemsView();
        activeView.SelectedItems.Clear();
        activeView.SelectedItems.Add(item);
        UpdateSelectionCommandBar();
        RefreshItemSelectionVisuals();
        ShowFeedback(new WidgetFeedbackRequest(
            T("Widget.SavedHere"),
            WidgetFeedbackSeverity.Success,
            "file-saved-here"));
    }

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearancePreview();
        ApplyAccentVisuals();
        ApplySelectionRectangleAppearance();
        UpdateItemSurfaceVisuals();
        UpdateStackFolderPreviewModes();
        UpdateStackPopoverAppearance();
        UpdateEmptyState();
    }

    private void FileSurfaceContent_ActualThemeChanged(
        FrameworkElement sender,
        object args)
    {
        ApplyAccentVisuals();
        ApplySelectionRectangleAppearance();
        UpdateItemSurfaceVisuals();
        UpdateStackPopoverAppearance();
    }

    private void ApplyAccentVisuals()
    {
        var accent = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        ReorderInsertionAccentStop.Color = accent;
        ReorderInsertionLine.Background = SharedBrushCache.GetOrCreate(accent);
        ImportProgressBar.Foreground = SharedBrushCache.GetOrCreate(accent);
        if (_activeImportVisualState is not ImportCompletionState.Failed)
        {
            ImportStateIcon.Foreground = SharedBrushCache.GetOrCreate(accent);
        }
    }

    public void OnActivated()
    {
        bool pointerActivation = Win32Helper.IsAnyMouseButtonDown();
        if (IsLoaded && !pointerActivation)
        {
            Root.Focus(FocusState.Programmatic);
        }
        App.LogVerbose(
            $"[FileStack] Surface activated widget={WidgetId} " +
            $"pointerActivation={pointerActivation} " +
            $"focusedRoot={IsLoaded && !pointerActivation}");

        if (_isWindowRevealCompleted)
        {
            QueueDiskReconciliationIfStale("activated");
        }
    }

    public void PrepareForReuse()
    {
        CloseStackPopover(releaseImmediately: true);
        // A group member can stay detached while its source items or settings
        // change. Clear recycled selector state first, then rebuild the stack
        // projection before the cached surface is attached again.
        ResetSelectionForStackProjectionChange();
        ResetStackInteractionVisuals();
        PersistSurfaceReorder();
        ResetDragPayloadCache();
        ViewModel.StabilizeStackDisplay();
    }

    public void OnDeactivated()
    {
        // File hydration and folder watchers follow the actual window visibility,
        // rather than foreground activation. Desktop-layer groups intentionally
        // use SW_SHOWNOACTIVATE, so treating their initial inactive state as a
        // deactivation would cancel the first icon hydration pass.
        //
        // Selection, however, is an interaction-scoped state: leaving DeskBox
        // (another app or the desktop takes the foreground) ends the selection
        // gesture, so stale highlights do not survive a round trip.
        ClearItemSelectionIfInteractionIdle();
    }

    public void OnCompactStateChanged(bool collapsed)
    {
        // Collapsing a widget hides its items; keeping hidden selection state
        // would resurrect stale highlights on the next expand.
        if (collapsed)
        {
            ClearItemSelectionIfInteractionIdle();
        }
    }

    private void ClearItemSelectionIfInteractionIdle()
    {
        // Active gestures own their selection: the stack popover, its drag
        // and title editing, an inline item rename, or an outbound drag all
        // read the current selection and must not observe a surprise reset
        // triggered by their own window activations.
        if (IsStackPopoverInteractionActive ||
            _stackPopoverDragActive ||
            _stackPopoverTitleEditing ||
            _itemRenameTarget is not null ||
            _pendingPointerDragItems.Length > 0)
        {
            return;
        }

        if (ItemsGrid.SelectedItems.Count == 0 &&
            ItemsList.SelectedItems.Count == 0 &&
            !_isBoxSelecting)
        {
            return;
        }

        ResetBoxSelectionState();
        ClearItemSelection();
    }

    public object? CaptureTransientState()
    {
        return new FileWidgetTransientState(
            GetSelectedItems()
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _cutClipboardPaths.ToArray());
    }

    public void RestoreTransientState(object? state)
    {
        if (state is not FileWidgetTransientState fileState)
        {
            return;
        }

        RestoreSelection(ItemsGrid, fileState.SelectedPaths);
        RestoreSelection(ItemsList, fileState.SelectedPaths);
        _cutClipboardPaths = fileState.CutPaths
            .Where(path => ViewModel.Items.Any(item =>
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        ApplyCutState();
        RefreshItemSelectionVisuals();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (visible)
        {
            _hasBeenWindowVisible = true;
            UpdateEmptyState();
            return;
        }

        _isWindowRevealCompleted = false;
        StopFolderNavigationVisuals();
        CloseStackPopover(releaseImmediately: true);

        // Content is attached before its host is shown, and the host reports its
        // initial hidden state during that attach. Do not cancel the initial
        // hydration in that case; only a real visible -> hidden transition
        // suspends the file surface.
        if (_hasBeenWindowVisible)
        {
            ViewModel.SuspendBackgroundActivity();
        }
    }

    private void QueueDiskReconciliationIfStale(
        string reason,
        bool hasDeferredChanges = false)
    {
        if (_isDisposed ||
            !FileSurfaceRefreshPolicy.ShouldReconcile(
                DateTime.UtcNow,
                _lastDiskReconciliationUtc,
                hasDeferredChanges) ||
            Interlocked.Exchange(ref _diskReconciliationQueued, 1) != 0)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // A desktop-pinned window can receive Activated while the
                    // mouse button which activated it is still held down.  A
                    // refresh at that point rebuilds the ItemsSource and
                    // unloads the pressed stack container before PointerReleased
                    // (and therefore ItemClick) can be delivered.  Keep the
                    // projection stable until the native pointer sequence has
                    // completed, then perform the same reconciliation.
                    await WaitForPointerSequenceToFinishAsync(
                        _lifetimeCancellation.Token);

                    if (_isDisposed || !_isWindowVisible || !_isWindowRevealCompleted)
                    {
                        return;
                    }

                    await RefreshAsync();
                    App.LogVerbose(
                        $"[FolderRefresh] Reconciled file surface " +
                        $"widget={WidgetId} reason={reason}");
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[FolderRefresh] File surface reconciliation failed " +
                        $"widget={WidgetId} reason={reason}: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _diskReconciliationQueued, 0);
                }
            }))
        {
            Interlocked.Exchange(ref _diskReconciliationQueued, 0);
        }
    }

    private static async Task WaitForPointerSequenceToFinishAsync(
        CancellationToken cancellationToken)
    {
        while (Win32Helper.IsAnyMouseButtonDown())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(16), cancellationToken);
        }

        // Let the matching PointerReleased/ItemClick routed events drain before
        // a refresh is allowed to recycle the item containers.
        await Task.Delay(TimeSpan.FromMilliseconds(48), cancellationToken);
    }

    public Task AddFromTitleButtonAsync() => RunAsync(PickAndImportFilesAsync);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HideInactiveScrollBars();
        ApplySelectionRectangleAppearance();
        UpdateEmptyState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopFolderNavigationVisuals();
        CloseStackPopover(releaseImmediately: true);
        StopScrollBarHideTimer();
        ResetStackInteractionVisuals();
        PersistSurfaceReorder();
        ResetDragPayloadCache();
        App.Current.WidgetManager?.NotifyQuickLookSurfaceUnavailable(this);
    }

    private void Items_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        ReconcileCutStateAfterItemsChanged(e);
        QueueStackPopoverReconciliation();
        UpdateEmptyState();
    }

    private void ReconcileCutStateAfterItemsChanged(
        NotifyCollectionChangedEventArgs e)
    {
        WidgetItem[] removedItems = e.OldItems?
            .OfType<WidgetItem>()
            .ToArray() ?? [];
        if (removedItems.Length > 0)
        {
            string[] replacementPaths = e.NewItems?
                .OfType<WidgetItem>()
                .Select(item => item.Path)
                .ToArray() ?? [];
            _cutClipboardPaths = FileCutStatePolicy.RemoveDepartedPaths(
                _cutClipboardPaths,
                removedItems.Select(item => item.Path),
                replacementPaths);

            foreach (WidgetItem item in removedItems)
            {
                item.IsCut = false;
            }
        }

        // Recompute every remaining item so newly inserted or rebound surfaces
        // never inherit a previous container's cut appearance.
        ApplyCutState();
    }

    private void UpdateEmptyState()
    {
        if (!IsLoaded)
        {
            return;
        }

        EmptyState.Visibility =
            !ViewModel.IsLoading && !ViewModel.VisibleItems.Any()
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ToggleViewButton_Click(object sender, RoutedEventArgs e)
    {
        string[] selectedPaths = GetSelectedItems()
            .Select(item => item.Path)
            .ToArray();
        ViewModel.ToggleViewMode();
        DispatcherQueue.TryEnqueue(() =>
        {
            ListViewBase activeView =
                ViewModel.IconViewVisibility == Visibility.Visible
                    ? ItemsGrid
                    : ItemsList;
            RestoreSelection(activeView, selectedPaths);
            UpdateSelectionCommandBar();
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(RefreshAsync);
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(PickAndImportFilesAsync);
    }

    private async void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WidgetStackItem stack)
        {
            bool shouldActivate =
                _stackInputActivation.ShouldActivateFromItemClick(
                    stack.StackKey);
            App.LogVerbose(
                $"[FileStack] ItemClick widget={WidgetId} " +
                $"stack={stack.StackKey} activate={shouldActivate}");
            if (shouldActivate)
            {
                ToggleStackFromInput(stack);
            }
            return;
        }

        if (e.ClickedItem is not WidgetItem item)
        {
            return;
        }

        bool controlPressed =
            Win32Helper.IsKeyPressed(VirtualKey.Control);
        bool shiftPressed =
            Win32Helper.IsKeyPressed(VirtualKey.Shift);

        if (!_settingsService.Settings.DoubleClickToOpen &&
            !controlPressed &&
            !shiftPressed)
        {
            bool fromStackPopover =
                ReferenceEquals(sender, _stackPopoverItemsView);
            await ActivateItemAsync(item);
            if (fromStackPopover)
            {
                CloseStackPopover(releaseImmediately: true);
            }
        }
    }

    public void OnWindowRevealCompleted()
    {
        if (_isDisposed || !_isWindowVisible || _isWindowRevealCompleted)
        {
            return;
        }

        _isWindowRevealCompleted = true;
        bool hasDeferredChanges = ViewModel.ResumeBackgroundActivity();
        QueueDiskReconciliationIfStale(
            "reveal-completed",
            hasDeferredChanges);
    }

    public void OnWindowLongHidden()
    {
        if (_isDisposed || _isWindowVisible)
        {
            return;
        }

        CloseStackPopover(releaseImmediately: true);
        ViewModel.ReleaseBackgroundActivityForLongHide();
    }

    private void ToggleStackFromInput(WidgetStackItem stack)
    {
        long now = Environment.TickCount64;
        if (string.Equals(
                _lastStackInputKey,
                stack.StackKey,
                StringComparison.Ordinal) &&
            now - _lastStackInputTick < StackDuplicateInputWindowMs)
        {
            return;
        }

        _lastStackInputKey = stack.StackKey;
        _lastStackInputTick = now;
        if (ViewModel.UsesStackPopover)
        {
            ToggleStackPopover(stack);
        }
        else
        {
            RequestStackState(
                stack,
                !GetDesiredStackState(stack));
        }
    }


    private async void Items_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        if (!_settingsService.Settings.DoubleClickToOpen ||
            FindItemFromSource(e.OriginalSource) is not { } item)
        {
            return;
        }

        if (item is WidgetStackItem)
        {
            e.Handled = true;
            return;
        }

        bool fromStackPopover =
            ReferenceEquals(sender, _stackPopoverItemsView);
        await ActivateItemAsync(item);
        if (fromStackPopover)
        {
            CloseStackPopover(releaseImmediately: true);
        }
        e.Handled = true;
    }

    private void Items_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        WidgetItem? item = FindItemFromSource(e.OriginalSource);
        if (item is null)
        {
            _lastItemContextScreenPoint = null;
            ClearSelection();
            FrameworkElement contentTarget =
                sender as FrameworkElement ?? Root;
            CreateContentAreaFlyout().ShowAt(
                contentTarget,
                e.GetPosition(contentTarget));
            e.Handled = true;
            return;
        }

        if (Win32Helper.GetCursorPos(out Win32Helper.POINT contextPoint))
        {
            _lastItemContextScreenPoint = (contextPoint.X, contextPoint.Y);
        }
        else
        {
            _lastItemContextScreenPoint = null;
        }

        ListViewBase activeView = GetActiveItemsView();
        if (!activeView.SelectedItems.Contains(item))
        {
            activeView.SelectedItems.Clear();
            activeView.SelectedItems.Add(item);
        }

        MenuFlyout flyout = item is WidgetStackItem stack
            ? CreateStackFlyout(stack)
            : GetSelectedItems().Count > 1
                ? CreateMultiSelectionFlyout()
                : CreateItemFlyout(item);
        flyout.Closed += (_, _) => _lastItemContextScreenPoint = null;
        bool fromStackPopover =
            ReferenceEquals(sender, _stackPopoverItemsView);
        if (fromStackPopover)
        {
            _stackPopoverContextMenuOpen = true;
            flyout.Closed += (_, _) =>
            {
                if (!_stackPopoverSystemContextMenuOpen)
                {
                    CompleteStackPopoverContextMenu();
                }
            };
        }
        if (item is WidgetStackItem)
        {
            flyout.Closed += (_, _) =>
            {
                ItemsGrid.SelectedItems.Remove(item);
                ItemsList.SelectedItems.Remove(item);
            };
        }
        FrameworkElement target =
            FindItemElement(e.OriginalSource) ??
            sender as FrameworkElement ??
            Root;
        try
        {
            flyout.ShowAt(target, e.GetPosition(target));
        }
        catch
        {
            if (fromStackPopover)
            {
                CompleteStackPopoverContextMenu();
            }
            throw;
        }
        e.Handled = true;
    }

    private void Items_DragItemsStarting(
        object sender,
        DragItemsStartingEventArgs e)
    {
        bool fromStackPopover =
            ReferenceEquals(sender, _stackPopoverItemsView);
        if (fromStackPopover)
        {
            _stackPopoverDragActive = true;
        }
        _activeDragHandledAsStackMembership = false;

        if (_isImportBusy)
        {
            e.Cancel = true;
            _pendingPointerDragItems = [];
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            if (fromStackPopover)
            {
                CompleteStackPopoverDrag();
            }
            return;
        }

        WidgetStackItem[] busyStacks = e.Items
            .OfType<WidgetStackItem>()
            .Where(stack => stack.Members.Any(member =>
                GetTransferState(member).BlocksMutation))
            .ToArray();
        if (busyStacks.Length > 0)
        {
            WidgetItem busyMember = busyStacks[0].Members.First(member =>
                GetTransferState(member).BlocksMutation);
            e.Cancel = true;
            ShowTransferBlockedFeedback(GetTransferState(busyMember));
            _pendingPointerDragItems = [];
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            if (fromStackPopover)
            {
                CompleteStackPopoverDrag();
            }

            return;
        }

        _activeDragSourcePaths = [];
        _activeDragHasStorageItems = false;
        _activeDragHandledAsStackMembership = false;
        ResetDragPayloadCache();
        ClearFolderDropTarget();
        HideSurfaceReorderInsertionIndicator();
        _isSurfaceReorderDragActive = false;
        _surfaceReorderPaths = [];
        _surfaceReorderStackKey = null;
        _surfaceReorderInsertionIndex = -1;
        _surfaceReorderLastPosition = default;
        _surfaceReorderHasLastPosition = false;
        _surfaceReorderDraggedItem = null;
        _surfaceReorderPathSet = null;
        _surfaceReorderLastView = null;
        WidgetStackItem? stack =
            e.Items.OfType<WidgetStackItem>().FirstOrDefault();
        if (stack is not null)
        {
            _pendingPointerDragItems = [];
            _stackPointerDragStarted = true;
            e.Data.RequestedOperation = DataPackageOperation.Link;
            e.Data.Properties[
                DeskBoxDragData.SourceWidgetIdProperty] = WidgetId;
            e.Data.Properties[
                DeskBoxDragData.InternalFileDragTokenProperty] =
                DeskBoxDragData.InternalFileDragToken;
            e.Data.Properties[
                DeskBoxDragData.StackReorderKeyProperty] =
                stack.StackKey;
            e.Data.Properties.Title = stack.Name;
            e.Data.SetText(stack.Name);
            return;
        }

        WidgetItem[] eventItems = e.Items
            .OfType<WidgetItem>()
            .ToArray();
        IReadOnlyList<WidgetItem> pointerSelection =
            _pendingPointerDragItems.Length > 1
                ? _pendingPointerDragItems
                : GetSelectedItems();
        _pendingPointerDragItems = [];
        WidgetItem[] selectedItems = FileItemDragPackage.ResolveDraggedItems(
                eventItems,
                pointerSelection)
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Path) &&
                (File.Exists(item.Path) || Directory.Exists(item.Path)))
            .ToArray();
        if (TryBlockTransferMutation(selectedItems))
        {
            e.Cancel = true;
            if (fromStackPopover)
            {
                CompleteStackPopoverDrag();
            }

            return;
        }

        if (!FileItemDragPackage.TryPrepare(
                e.Data,
                selectedItems,
                WidgetId,
                paths => _fileService.GetStorageItems(paths),
                paths => paths.Count == 1
                    ? Path.GetFileName(paths[0])
                    : paths.Count.ToString(),
                out FileItemDragPackageResult result))
        {
            e.Cancel = true;
            if (fromStackPopover)
            {
                CompleteStackPopoverDrag();
            }
            return;
        }

        _activeDragSourcePaths = result.SourcePaths.ToArray();
        _activeDragHasStorageItems = result.HasStorageItems;
        if (fromStackPopover &&
            !string.IsNullOrWhiteSpace(_stackPopoverKey))
        {
            e.Data.Properties[
                DeskBoxDragData.SourceStackKeyProperty] =
                _stackPopoverKey;
        }
    }

    private void Items_DragStarting(
        UIElement sender,
        DragStartingEventArgs e)
    {
        string[] sourcePaths = _activeDragSourcePaths.Length > 0
            ? _activeDragSourcePaths
            : GetSelectedItems()
                .Where(item => item is not WidgetStackItem)
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        if (HasActiveTransferSource(sourcePaths))
        {
            e.Cancel = true;
            ShowTransferBlockedFeedback(
                _fileService.TransferSessions.GetState(sourcePaths[0]));
            return;
        }
        if (sourcePaths.Length > 0)
        {
            // Use the system-provided file visual instead of WinUI's item-card
            // snapshot. This keeps widget-to-widget drags visually identical
            // to an Explorer file drag while preserving the same DataPackage.
            e.DragUI.SetContentFromDataPackage();
        }

        if (!ViewModel.FollowsDefaultStoragePath ||
            !NativeShellFileDragProvider.AreExistingShortcuts(sourcePaths))
        {
            return;
        }

        // DataPackage.RequestedOperation is a single preferred operation,
        // while AllowedOperations controls the permitted set. Managed storage
        // shortcuts are being restored to the desktop, so both are Move.
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.AllowedOperations = DataPackageOperation.Move;
    }

    private void Items_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs e)
    {
        bool fromStackPopover =
            ReferenceEquals(sender, _stackPopoverItemsView);
        string[] movedPaths = _activeDragSourcePaths.Length > 0
            ? _activeDragSourcePaths
            : e.Items
                .OfType<WidgetItem>()
                .Where(item => item is not WidgetStackItem)
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        bool hasStorageItems = _activeDragHasStorageItems;
        bool handledAsStackMembership =
            _activeDragHandledAsStackMembership;
        _activeDragSourcePaths = [];
        _activeDragHasStorageItems = false;
        _activeDragHandledAsStackMembership = false;

        try
        {
            if (ShouldObserveExternalDragOut(
                    e.DropResult,
                    hasStorageItems,
                    handledAsStackMembership,
                    fromStackPopover) &&
                movedPaths.Length > 0)
            {
                // DropResult describes the target's requested operation, not an
                // item-by-item completion result. Reconcile against a successful
                // parent enumeration so a partial/cancelled Shell move cannot
                // remove every original row.
                _ = ObserveExternalDragOutAsync(
                    movedPaths,
                    _lifetimeCancellation.Token);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Drag completion refresh failed " +
                $"id={WidgetId}: {ex}");
        }
        finally
        {
            _pressedStack = null;
            _stackPointerDragStarted = false;
            _stackInputActivation.CancelPointer();
            ClearFolderDropTarget();
            ClearStackMemberDropTarget();
            if (!fromStackPopover &&
                _isSurfaceReorderDragActive &&
                _surfaceReorderHasLastPosition)
            {
                // WinUI can complete an item drag without raising Drop. The
                // last DragOver position is still the release position, so
                // commit once here instead of losing the reorder.
                CommitSurfaceReorder(_surfaceReorderLastPosition);
            }
            else
            {
                PersistSurfaceReorder();
            }

            if (fromStackPopover)
            {
                CompleteStackPopoverDrag();
            }
        }
    }

    internal static bool ShouldObserveExternalDragOut(
        DataPackageOperation dropResult,
        bool hasStorageItems,
        bool handledAsStackMembership,
        bool fromStackPopover)
    {
        if (handledAsStackMembership)
        {
            return false;
        }

        if (dropResult == DataPackageOperation.Move)
        {
            return true;
        }

        // The Shell can occasionally report None for an accepted drag from
        // the main file surface, so keep its existing delayed reconciliation.
        // A popover member drag uses None for cancellation and for membership
        // no-ops; observing that result can leave a long-running stale probe
        // that later mistakes an unrelated move for this drag.
        return !fromStackPopover &&
            dropResult == DataPackageOperation.None &&
            hasStorageItems;
    }

    private async Task ObserveExternalDragOutAsync(
        IReadOnlyCollection<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        var remainingPaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (remainingPaths.Count == 0)
        {
            return;
        }

        int delayMs = 300;
        const int MaxAttempts = 11;
        try
        {
            for (int attempt = 0;
                 attempt < MaxAttempts &&
                 !_isDisposed &&
                 remainingPaths.Count > 0;
                 attempt++)
            {
                await Task.Delay(delayMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<string> missingPaths =
                    await ViewModel.GetConfirmedMissingPathsAsync(remainingPaths);
                if (missingPaths.Count > 0)
                {
                    await ViewModel.HandleItemsMovedOutAsync(missingPaths);
                    foreach (string path in missingPaths)
                    {
                        remainingPaths.Remove(path);
                    }

                    // Re-read the directory as a reconciliation step. This covers
                    // batched Shell moves and folder-watcher notifications that were
                    // coalesced while the grouped Surface was inactive.
                    await ViewModel.RefreshFolderContentsAsync();
                    UpdateEmptyState();
                    App.Log(
                        $"[WidgetSurface] External drag-out reconciled " +
                        $"id={WidgetId} removed={missingPaths.Count} " +
                        $"remaining={remainingPaths.Count}");
                }

                delayMs = (int)Math.Min(delayMs * 2, 300_000);
            }
        }
        catch (OperationCanceledException)
        {
            // The Surface was replaced, its group switched member, or the app closed.
        }
        catch (ObjectDisposedException)
        {
            // The content host disposed the member while a Shell move was pending.
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] External drag-out reconciliation failed " +
                $"id={WidgetId}: {ex}");
        }
    }

    private async Task RenameItemAsync(WidgetItem item)
    {
        if (TryBlockTransferMutation([item]))
        {
            return;
        }

        if (IsItemInStackPopover(item))
        {
            await RenameStackPopoverItemAsync(item);
            return;
        }

        // Let the MenuFlyout finish closing before taking keyboard focus.
        await Task.Yield();
        await StartItemRenameAsync(item);
    }

    private async Task RenameStackPopoverItemAsync(WidgetItem item)
    {
        string? stackKey = _stackPopoverKey;
        CloseStackPopover(releaseImmediately: true);
        await Task.Yield();
        if (_isDisposed || Root.XamlRoot is null)
        {
            return;
        }

        var textBox = new TextBox
        {
            Text = item.Name,
            MinWidth = 280
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = T("Common.Rename"),
            Content = textBox,
            PrimaryButtonText = T("Common.Save"),
            CloseButtonText = T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled =
                !string.IsNullOrWhiteSpace(textBox.Text)
        };
        textBox.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled =
                !string.IsNullOrWhiteSpace(textBox.Text);

        App.Current?.WidgetManager?.BeginWidgetInteraction(
            "surface-stack-popover-item-rename-opened");
        try
        {
            Windows.Foundation.IAsyncOperation<ContentDialogResult> operation =
                dialog.ShowAsync();
            DispatcherQueue.TryEnqueue(() =>
                SelectItemNameForRename(textBox, item.IsFolder));
            ContentDialogResult result = await operation;
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string newName = textBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                return;
            }

            await ViewModel.RenameItemAsync(item, newName);
            if (stackKey is not null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isDisposed &&
                        ViewModel.UsesStackPopover &&
                        ViewModel.FindStackByKey(stackKey) is { } current)
                    {
                        ShowStackPopover(current);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Popover item rename failed " +
                $"id={WidgetId}: {ex}");
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.RenameFailed"),
                WidgetFeedbackSeverity.Error,
                "file-rename-error"));
        }
        finally
        {
            App.Current?.WidgetManager?.EndWidgetInteraction(
                "surface-stack-popover-item-rename-closed");
        }
    }

    private async Task RenameStackAsync(WidgetStackItem stack)
    {
        string stackKey = stack.StackKey;
        // Let the MenuFlyout finish closing before taking keyboard focus.
        await Task.Yield();
        WidgetStackItem? currentStack = ViewModel.FindStackByKey(stackKey);
        if (currentStack is null)
        {
            App.Log(
                $"[WidgetSurface] Stack rename target disappeared " +
                $"id={WidgetId} key={stackKey}");
            return;
        }

        await StartItemRenameAsync(currentStack, stackKey);
    }

    private async Task StartItemRenameAsync(
        WidgetItem item,
        string? stableStackKey = null)
    {
        FrameworkElement? target = stableStackKey is not null
            ? await FindOrRealizeStackRenameTargetAsync(stableStackKey)
            : await FindOrRealizeItemRenameTargetAsync(item);
        UIElement? contentHost = SelectionOverlay.Parent as UIElement;
        if (target is null || contentHost is null)
        {
            App.Log(
                $"[WidgetSurface] Inline rename target unavailable " +
                $"id={WidgetId} target={item.Name}");
            return;
        }

        WidgetItem renameItem = stableStackKey is not null
            ? ViewModel.FindStackByKey(stableStackKey) ?? item
            : target.DataContext as WidgetItem ??
              FindDisplayedItem(item) ??
              item;
        FrameworkElement? nameElement = FindItemNameElement(renameItem);

        ListViewBase activeView = GetActiveItemsView();
        activeView.SelectedItems.Clear();
        activeView.SelectedItems.Add(renameItem);
        _itemRenameTarget = renameItem;
        _itemRenameStackKey = stableStackKey;
        _isCancellingItemRename = false;
        ItemRenameTextBox.Text = renameItem.Name;

        if (nameElement is TextBlock nameText)
        {
            _itemRenameNameText = nameText;
            nameText.Visibility = Visibility.Collapsed;
            ItemRenameTextBox.FontSize =
                nameText.FontSize > 0 ? nameText.FontSize : 14;
            ItemRenameTextBox.TextAlignment = nameText.TextAlignment;
            ItemRenameTextBox.HorizontalContentAlignment =
                nameText.HorizontalAlignment switch
                {
                    HorizontalAlignment.Center => HorizontalAlignment.Center,
                    HorizontalAlignment.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left
                };
            ItemRenameTextBox.TextWrapping = nameText.TextWrapping;
        }
        else
        {
            ItemRenameTextBox.FontSize = ViewModel.IsListMode
                ? ViewModel.ListLabelFontSize
                : ViewModel.IconLabelFontSize;
            ItemRenameTextBox.TextAlignment = ViewModel.IsListMode
                ? TextAlignment.Left
                : TextAlignment.Center;
            ItemRenameTextBox.TextWrapping = TextWrapping.NoWrap;
        }

        PositionItemRenameTextBox(target, contentHost);
        ItemRenameTextBox.Visibility = Visibility.Visible;
        ItemRenameTextBox.IsHitTestVisible = true;
        App.Current?.WidgetManager?.BeginWidgetInteraction(
            "surface-file-item-rename-opened");

        SelectItemNameForRename(
            ItemRenameTextBox,
            renameItem is WidgetStackItem || renameItem.IsFolder);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_itemRenameTarget, renameItem))
            {
                SelectItemNameForRename(
                    ItemRenameTextBox,
                    renameItem is WidgetStackItem || renameItem.IsFolder);
            }
        });

        await Task.CompletedTask;
    }

    private async void ItemRenameTextBox_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitItemRenameAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CancelItemRename();
        }
    }

    private async void ItemRenameTextBox_LostFocus(
        object sender,
        RoutedEventArgs e)
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
            if (_itemRenameStackKey is { } stackKey)
            {
                if (ViewModel.FindStackByKey(stackKey) is null)
                {
                    throw new InvalidOperationException(
                        _localizationService.T("Widget.Stack.RenameUnavailable"));
                }

                ViewModel.SetStackNameOverride(stackKey, newName);
            }
            else
            {
                if (TryBlockTransferMutation(_itemRenameTarget))
                {
                    CompleteItemRename();
                    return;
                }

                await ViewModel.RenameItemAsync(_itemRenameTarget, newName);
            }
            CompleteItemRename();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Inline rename failed id={WidgetId}: {ex}");
            ShowFeedback(new WidgetFeedbackRequest(
                _localizationService.T("Widget.RenameFailed"),
                WidgetFeedbackSeverity.Error,
                "file-rename-error"));
            // End the failed edit instead of restoring focus to the editor.
            // Restoring focus can raise LostFocus again and resubmit the same
            // blocked rename while another application still owns a file.
            CompleteItemRename();
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
        if (_itemRenameNameText is not null)
        {
            _itemRenameNameText.Visibility = Visibility.Visible;
            _itemRenameNameText = null;
        }

        _itemRenameTarget = null;
        _itemRenameStackKey = null;
        App.Current?.WidgetManager?.EndWidgetInteraction(
            "surface-file-item-rename-closed");
    }

    private void PositionItemRenameTextBox(
        FrameworkElement target,
        UIElement contentHost)
    {
        Windows.Foundation.Point topLeft = target
            .TransformToVisual(contentHost)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        const double border = 1;
        const double horizontalPadding = 2;
        double targetLeft = topLeft.X;
        double offsetX = targetLeft - border - horizontalPadding;
        double offsetY = topLeft.Y - border;
        double hostPaddingHorizontal = 0;
        double hostPaddingVertical = 0;
        if (contentHost is Grid grid)
        {
            hostPaddingHorizontal = grid.Padding.Left + grid.Padding.Right;
            hostPaddingVertical = grid.Padding.Top + grid.Padding.Bottom;
            targetLeft -= grid.Padding.Left;
            offsetX = targetLeft - border - horizontalPadding;
            offsetY -= grid.Padding.Top;
        }

        double height = Math.Max(target.ActualHeight + (2 * border), 20);
        double width;
        if (contentHost is FrameworkElement host)
        {
            double contentWidth =
                Math.Max(60, host.ActualWidth - hostPaddingHorizontal);
            double availableWidth =
                Math.Max(60, contentWidth - offsetX - 8);
            width = ViewModel.IsListMode
                ? Math.Clamp(availableWidth, 80, contentWidth)
                : Math.Clamp(
                    target.ActualWidth +
                    (2 * (border + horizontalPadding)),
                    60,
                    availableWidth);
            double contentHeight =
                Math.Max(20, host.ActualHeight - hostPaddingVertical);
            height = Math.Min(
                height,
                Math.Max(20, contentHeight - offsetY - 4));

            if (!ViewModel.IsListMode)
            {
                // Icon labels are often narrower than the editor's minimum
                // width. Growing only toward the right visibly detaches the
                // editor from the icon, so keep both centers aligned and only
                // clamp when the item is next to a surface edge.
                offsetX = targetLeft + ((target.ActualWidth - width) / 2);
                offsetX = Math.Clamp(
                    offsetX,
                    0,
                    Math.Max(0, contentWidth - width));
            }
        }
        else
        {
            width = Math.Max(
                target.ActualWidth +
                (2 * (border + horizontalPadding)),
                60);
        }

        ItemRenameTextBox.Width = width;
        ItemRenameTextBox.Height = height;
        ItemRenameTextBox.Margin =
            new Thickness(offsetX, offsetY, 0, 0);
    }

    private FrameworkElement? FindItemNameElement(WidgetItem item)
    {
        if (item is WidgetStackItem stack)
        {
            return FindStackNameElement(stack.StackKey);
        }

        if (GetActiveItemsView().ContainerFromItem(item)
            is not SelectorItem container)
        {
            return null;
        }

        return FindItemSurface(item) is Border border
            ? FileItemSurface.FindOwner(border)?.ItemNameText
            : null;
    }

    private async Task<FrameworkElement?>
        FindOrRealizeItemRenameTargetAsync(WidgetItem item)
    {
        const int realizationPasses = 5;
        ViewModel.RevealItemForInteraction(item.Path);
        for (int pass = 0; pass < realizationPasses; pass++)
        {
            ListViewBase activeView = GetActiveItemsView();
            WidgetItem? displayedItem = FindDisplayedItem(item);
            if (_isDisposed)
            {
                return null;
            }

            if (displayedItem is not null)
            {
                // The new item can sort outside the current viewport. Always
                // reveal the projected item before asking for its container.
                activeView.ScrollIntoView(displayedItem);
                activeView.UpdateLayout();
                FrameworkElement? target =
                    FindItemNameElement(displayedItem) ??
                    FindItemSurface(displayedItem);
                if (target is not null)
                {
                    return target;
                }
            }

            if (!await YieldForItemContainerRealizationAsync())
            {
                break;
            }
        }

        WidgetItem? finalItem = FindDisplayedItem(item);
        return finalItem is null
            ? null
            : FindItemNameElement(finalItem) ??
              FindItemSurface(finalItem);
    }

    private async Task<FrameworkElement?>
        FindOrRealizeStackRenameTargetAsync(string stackKey)
    {
        const int realizationPasses = 5;
        for (int pass = 0; pass < realizationPasses; pass++)
        {
            if (_isDisposed)
            {
                return null;
            }

            ListViewBase activeView = GetActiveItemsView();
            WidgetStackItem? stack = ViewModel.FindStackByKey(stackKey);
            if (stack is null)
            {
                return null;
            }

            activeView.ScrollIntoView(stack);
            activeView.UpdateLayout();
            FrameworkElement? target = FindStackNameElement(stackKey) ??
                FindStackSurface(stackKey);
            if (target is not null)
            {
                return target;
            }

            if (!await YieldForItemContainerRealizationAsync())
            {
                break;
            }
        }

        return FindStackNameElement(stackKey) ?? FindStackSurface(stackKey);
    }

    private Border? FindStackSurface(string stackKey) =>
        _stackSurfaces.FirstOrDefault(surface =>
            surface.DataContext is WidgetStackItem stack &&
            string.Equals(
                stack.StackKey,
                stackKey,
                StringComparison.Ordinal));

    private FrameworkElement? FindStackNameElement(string stackKey)
    {
        Border? surface = FindStackSurface(stackKey);
        return surface is null
            ? null
            : FindDescendantByTag(surface, "StackName");
    }

    private static FrameworkElement? FindDescendantByTag(
        DependencyObject parent,
        string tag)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element &&
                string.Equals(element.Tag as string, tag, StringComparison.Ordinal))
            {
                return element;
            }

            FrameworkElement? match = FindDescendantByTag(child, tag);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private Task<bool> YieldForItemContainerRealizationAsync()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () => completion.TrySetResult(!_isDisposed)))
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private static void SelectItemNameForRename(
        TextBox textBox,
        bool isFolder)
    {
        textBox.Focus(FocusState.Programmatic);
        string text = textBox.Text;
        if (isFolder)
        {
            textBox.SelectAll();
            return;
        }

        int dotIndex = text.LastIndexOf('.');
        if (dotIndex > 0 && text.Length - dotIndex - 1 <= 8)
        {
            textBox.Select(0, dotIndex);
        }
        else
        {
            textBox.SelectAll();
        }
    }

    private async Task DeleteItemAsync(WidgetItem item)
    {
        await DeleteItemsAsync([item]);
    }

    private void Root_ObserveHandledDragOver(object sender, DragEventArgs e)
    {
        // Folder and stack targets intentionally handle DragOver before it
        // reaches Root_DragOver. Observe those handled events as well so an
        // older target cannot remain highlighted after a fast child crossing.
        ClearStaleChildDropTargets(e);
    }

    private void Root_ObserveHandledDragLeave(object sender, DragEventArgs e)
    {
        // A child target also handles DragLeave. Without a handled-events-too
        // observer, a direct child-to-outside transition can bypass the root
        // cleanup path and strand its drop-target visual.
        if (!IsPointerInsideRoot(e))
        {
            ClearDragSessionVisualState();
            return;
        }

        ClearStaleChildDropTargets(e);
    }

    private void ClearStaleChildDropTargets(DragEventArgs e)
    {
        if (_folderDropTarget is { } folderTarget &&
            !IsPointerInsideDropElement(folderTarget, e))
        {
            ClearFolderDropTarget();
        }

        if (_stackMemberDropTarget is { } stackTarget &&
            !IsPointerInsideDropElement(stackTarget, e))
        {
            ClearStackMemberDropTarget();
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        ApplySurfaceDragOverFeedback(
            e,
            allowInternalReorderPreview: true);
    }

    internal void ApplyHostEdgeDragOverFeedback(DragEventArgs e)
    {
        ApplySurfaceDragOverFeedback(
            e,
            allowInternalReorderPreview: false);
    }

    private void ApplySurfaceDragOverFeedback(
        DragEventArgs e,
        bool allowInternalReorderPreview)
    {
        e.Handled = true;
        // Child folder/stack targets mark their own DragOver handled. Reaching
        // either the file root or the host's transparent resize edge therefore
        // means the pointer is no longer over either explicit child target.
        ClearFolderDropTarget();
        ClearStackMemberDropTarget();
        if (_isImportBusy)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            return;
        }

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        if (HasTransferConflict(payload.Paths, ViewModel.CurrentFolderPath))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            ApplyDropVisual(FileDropVisualState.None);
            return;
        }
        if (payload.IsStackPopoverMemberDrag)
        {
            PersistSurfaceReorder();
            bool canDetach = TryGetStackPopoverDragItems(
                payload,
                out _,
                out WidgetItem[] detachItems) &&
                detachItems.Length > 0;
            e.AcceptedOperation = canDetach
                ? DataPackageOperation.Link
                : DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = canDetach;
            e.DragUIOverride.IsCaptionVisible = canDetach;
            if (canDetach)
            {
                e.DragUIOverride.Caption = T("Widget.Stack.RemoveItem");
            }
            ApplyDropVisual(FileDropVisualState.None);
            return;
        }

        if (payload.IsInternalReorder)
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            ApplyDropVisual(FileDropVisualState.None);
            if (allowInternalReorderPreview)
            {
                HandleSurfaceRealTimeReorder(
                    payload,
                    e.GetPosition(GetActiveItemsView()));
            }
            return;
        }

        if (payload.HasSurfacePathData)
        {
            if (!payload.IsDeskBoxFileDrag)
            {
                SuppressExternalDragOperationBadge(e);
            }
            if (IsUnsafeFolderDrop(payload.Paths, ViewModel.CurrentFolderPath))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                if (payload.IsDeskBoxFileDrag)
                {
                    ApplyDeskBoxFileDragFeedback(
                        e,
                        DataPackageOperation.None,
                        T("Widget.Error.UnsafeFolderTransfer"));
                }
                ApplyDropVisual(FileDropVisualState.None);
                return;
            }

            // External shell drags keep their source-provided compact visual.
            // Setting DragUIOverride here replaces it with WinUI's larger card.
            FileDropIntent resolvedIntent = ResolveSurfaceDropIntent(
                payload.DataView);
            e.AcceptedOperation = ResolveSurfaceDropOperation(payload.DataView);
            if (payload.IsDeskBoxFileDrag)
            {
                string targetName = string.IsNullOrWhiteSpace(ViewModel.Name)
                    ? ViewModel.CurrentFolderDisplayName
                    : ViewModel.Name;
                ApplyDeskBoxFileDragFeedback(
                    e,
                    e.AcceptedOperation,
                    FormatDropCaption(resolvedIntent, targetName));
            }
            ApplyDropVisual(FileDropVisualState.None);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ApplyDropVisual(FileDropVisualState.None);
        }
    }

    private static void SuppressExternalDragOperationBadge(DragEventArgs e)
    {
        // Do not let XAML replace Explorer's source-sized drag image with its
        // larger content preview. The native OLE drop target and Shell drag
        // image manager own the icon, operation glyph, and target description.
        e.DragUIOverride.IsContentVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.IsCaptionVisible = false;
    }

    private static void ApplyDeskBoxFileDragFeedback(
        DragEventArgs e,
        DataPackageOperation operation,
        string caption)
    {
        bool canDrop = operation != DataPackageOperation.None;
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = canDrop;
        e.DragUIOverride.IsCaptionVisible = !string.IsNullOrWhiteSpace(caption);
        e.DragUIOverride.Caption = caption;
    }

    private bool IsUnsafeFolderDrop(
        IReadOnlyList<string> sourcePaths,
        string? destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return false;
        }

        string normalizedDestination;
        try
        {
            normalizedDestination = Path.GetFullPath(destinationFolder);
        }
        catch
        {
            return false;
        }

        if (_dragUnsafeDropCache.TryGetValue(
                normalizedDestination,
                out bool cachedResult))
        {
            return cachedResult;
        }

        bool unsafeDrop = false;
        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            string normalizedSource;
            try
            {
                normalizedSource = Path.GetFullPath(sourcePath);
            }
            catch
            {
                continue;
            }

            if (!_dragDirectoryCache.TryGetValue(
                    normalizedSource,
                    out bool isDirectory))
            {
                isDirectory = Directory.Exists(normalizedSource);
                _dragDirectoryCache[normalizedSource] = isDirectory;
            }

            if (isDirectory &&
                FileService.IsPathUnderDirectory(
                    normalizedDestination,
                    normalizedSource))
            {
                unsafeDrop = true;
                break;
            }
        }

        _dragUnsafeDropCache[normalizedDestination] = unsafeDrop;
        return unsafeDrop;
    }

    private void Root_DragEnter(object sender, DragEventArgs e)
    {
        if (_dragPayloadSessionActive &&
            _dragPayloadSnapshot is { } cached &&
            !IsSameDragPayload(e.DataView, cached))
        {
            ResetDragPayloadCache();
        }

        GetDragPayload(e.DataView);
        ApplyDropVisual(FileDropVisualState.None);
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        if (IsPointerInsideRoot(e))
        {
            return;
        }

        ClearFolderDropTarget();
        ClearStackMemberDropTarget();
        ApplyDropVisual(FileDropVisualState.None);
        // Leaving the surface means the user may be dragging to Explorer,
        // another widget or another application. Discard the internal preview;
        // only a confirmed drop back onto this surface may change ordering.
        PersistSurfaceReorder();
        ResetDragPayloadCache();
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        ClearFolderDropTarget();
        ClearStackMemberDropTarget();
        ApplyDropVisual(FileDropVisualState.None);
        if (_isImportBusy ||
            HasTransferConflict(payload.Paths, ViewModel.CurrentFolderPath))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            App.LogVerbose(
                $"[WidgetSurface] Ignored overlapping file drop id={WidgetId} " +
                "stage=before-read");
            if (HasTransferConflict(payload.Paths, ViewModel.CurrentFolderPath))
            {
                ShowTransferBlockedFeedback(
                    _fileService.TransferSessions.GetState(
                        payload.Paths.FirstOrDefault()) is { IsActive: true } sourceState
                        ? sourceState
                        : _fileService.TransferSessions.GetState(
                            ViewModel.CurrentFolderPath));
            }
            ResetDragPayloadCache();
            return;
        }

        if (payload.IsStackPopoverMemberDrag)
        {
            _activeDragHandledAsStackMembership = true;
            bool removed = false;
            if (TryGetStackPopoverDragItems(
                    payload,
                    out WidgetStackItem? sourceStack,
                    out WidgetItem[] detachItems))
            {
                ApplyStackProjectionChange(() =>
                    removed = ViewModel.RemoveItemsFromStack(
                        sourceStack.StackKey,
                        detachItems));
            }

            e.AcceptedOperation = removed
                ? DataPackageOperation.Link
                : DataPackageOperation.None;
            PersistSurfaceReorder();
            if (removed)
            {
                CloseStackPopover();
            }
            ResetDragPayloadCache();
            return;
        }

        if (payload.IsInternalReorder)
        {
            _surfaceReorderStackKey ??= TryGetString(
                e.DataView.Properties,
                DeskBoxDragData.StackReorderKeyProperty);
            HandleSurfaceFinalReorder(
                payload.Paths,
                e.GetPosition(GetActiveItemsView()));
            e.AcceptedOperation = DataPackageOperation.Link;
            PersistSurfaceReorder();
            ResetDragPayloadCache();
            return;
        }

        string dropOperationId = Guid.NewGuid().ToString("N")[..8];
        App.Log(
            $"[DropOperation] operation={dropOperationId} widget={WidgetId} " +
            "stage=Received");
        // DragOver may have left Move on the event. Keep the completion result
        // non-destructive until the filesystem transfer has actually finished.
        e.AcceptedOperation = DataPackageOperation.None;
        var deferral = e.GetDeferral();
        // Start visible feedback before asking the source application for its
        // StorageItems. Explorer, cloud providers and virtual-file sources can
        // spend seconds materializing a large payload before paths are
        // available; that preparation time is part of the import operation.
        BeginTrackedImport();
        try
        {
            using DroppedFileBatch batch = await GetSurfaceDropFilesAsync(e.DataView);
            IReadOnlyList<DroppedFilePath> droppedFiles = batch.Files;
            App.Log(
                $"[DropOperation] operation={dropOperationId} widget={WidgetId} " +
                $"stage=PayloadMaterialized count={droppedFiles.Count} " +
                $"skipped={batch.SkippedCount}");
            string[] paths = droppedFiles
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (HasTransferConflict(paths, ViewModel.CurrentFolderPath))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                ShowTransferBlockedFeedback(
                    _fileService.TransferSessions.GetState(
                        paths.FirstOrDefault()) is { IsActive: true } sourceState
                        ? sourceState
                        : _fileService.TransferSessions.GetState(
                            ViewModel.CurrentFolderPath));
                return;
            }
            if (droppedFiles.Count > 0)
            {
                // Modifier keys can change while the pointer is already over
                // the surface. Recompute at Drop instead of trusting a cached
                // DragOver result.
                FileDropIntent resolvedIntent = ResolveSurfaceDropIntent(
                    e.DataView,
                    forceCopy: droppedFiles.Any(file => file.ForceManagedCopy),
                    sourcePathsOverride: droppedFiles.Select(file => file.Path));
                DataPackageOperation accepted =
                    ToDataPackageOperation(resolvedIntent);
                if (accepted == DataPackageOperation.None)
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    return;
                }

                bool mapped = !string.IsNullOrWhiteSpace(
                    ViewModel.MappedFolderPath);
                bool? moveWhenMapped = mapped
                    ? accepted == DataPackageOperation.Move
                    : null;
                string? sourceWidgetId = TryGetString(
                    e.DataView.Properties,
                    "DeskBoxSourceWidgetId");

                IReadOnlyList<string> completedSourcePaths =
                    await ImportDroppedFilesAsync(
                        droppedFiles,
                        moveWhenMapped,
                        intentOverride: resolvedIntent == FileDropIntent.Shortcut
                            ? FileDropIntent.Shortcut
                            : null);
                App.Log(
                    $"[DropOperation] operation={dropOperationId} widget={WidgetId} " +
                    $"stage=ImportCompleted count={completedSourcePaths.Count}");
                if (moveWhenMapped == true &&
                    sourceWidgetId is { Length: > 0 } &&
                    App.Current?.WidgetManager is { } manager)
                {
                    await manager.NotifyItemsMovedOutAsync(
                        sourceWidgetId,
                        completedSourcePaths);
                }

                int requestedMoveCount = droppedFiles.Count(file =>
                    !file.ForceManagedCopy);
                e.AcceptedOperation = ResolveSafeDropCompletionOperation(
                    accepted,
                    payload.IsDeskBoxFileDrag,
                    requestedMoveCount,
                    completedSourcePaths.Count);

                int completedCount = moveWhenMapped == true
                    ? completedSourcePaths.Count
                    : droppedFiles.Count;
                ShowFeedback(moveWhenMapped == true && completedCount == 0
                    ? new(
                        T("Widget.NoItemsMoved"),
                        WidgetFeedbackSeverity.Warning,
                        "file-drop-empty")
                    : new(
                        _localizationService.Format(
                            moveWhenMapped == true
                                ? "Widget.MovedCount"
                                : "Widget.PastedCount",
                            completedCount),
                        WidgetFeedbackSeverity.Success,
                        "file-drop"));
            }
        }
        catch (OperationCanceledException)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            App.Log(
                $"[DropOperation] operation={dropOperationId} widget={WidgetId} " +
                "stage=Canceled");
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(
                    ImportCompletionState.Canceled);
            }
        }
        catch (Exception ex)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            App.Log(
                $"[DropOperation] operation={dropOperationId} widget={WidgetId} " +
                $"stage=Failed error={ex}");
            ShowFeedback(new(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "file-drop-error"));
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(
                    ImportCompletionState.Failed);
            }
        }
        finally
        {
            // Empty/unsupported payloads return before ImportDroppedFilesAsync
            // owns completion. Never leave their preparation session busy.
            if (_activeImportCancellation is not null)
            {
                CancelAndResetTrackedImport();
            }
            ApplyDropVisual(FileDropVisualState.None);
            ResetDragPayloadCache();
            deferral.Complete();
            App.Log(
                $"[DropOperation] operation={dropOperationId} widget={WidgetId} " +
                "stage=DeferralReleased");
        }
    }

    internal void HandleHostEdgeDrop(DragEventArgs e)
    {
        Root_Drop(Root, e);
    }

    private void SetImportBusy(bool isBusy)
    {
        SetBusyOverlay(
            isBusy,
            "Widget.Import.Title",
            "Widget.Import.Description");
    }

    internal void SetMigrationBusy(bool isBusy)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetMigrationBusy(isBusy));
            return;
        }

        SetBusyOverlay(
            isBusy,
            "Widget.Migration.Title",
            "Widget.Migration.Description");
    }

    internal void SetDesktopOrganizationBusy(bool isBusy)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetDesktopOrganizationBusy(isBusy));
            return;
        }

        SetBusyOverlay(
            isBusy,
            "DesktopOrganization.Busy.Title",
            "DesktopOrganization.Busy.Description");
    }

    private void SetBusyOverlay(
        bool isBusy,
        string titleKey,
        string descriptionKey)
    {
        if (_isImportBusy == isBusy)
        {
            return;
        }

        _isImportBusy = isBusy;
        if (isBusy)
        {
            _importBusyStartedAtUtc = DateTimeOffset.UtcNow;
            ImportTitleText.Text = T(titleKey);
            ImportDescriptionText.Text = T(descriptionKey);
            ImportProgressBar.Value = 0;
            ImportStateIcon.Glyph = "\uE896";
            ImportStateIcon.Foreground = ImportProgressBar.Foreground;
            ApplyDropVisual(FileDropVisualState.None);
        }

        ImportProgressCard.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        ImportProgressBar.IsIndeterminate = isBusy;
        ImportPercentText.Text = string.Empty;
        ImportCancelButton.Visibility = Visibility.Collapsed;
        SelectionCommandBar.IsEnabled = !isBusy;
        if (!isBusy)
        {
            _importBusyStartedAtUtc = null;
        }
        ImportBusyChanged?.Invoke(isBusy);
    }

    private DragPayloadSnapshot GetDragPayload(DataPackageView dataView)
    {
        if (_dragPayloadSessionActive && _dragPayloadSnapshot is { } cached)
        {
            return cached;
        }

        string[] paths = GetPackagePaths(dataView);
        string? sourceWidgetId = TryGetString(
            dataView.Properties,
            DeskBoxDragData.SourceWidgetIdProperty);
        string? internalDragToken = TryGetString(
            dataView.Properties,
            DeskBoxDragData.InternalFileDragTokenProperty);
        string? stackReorderKey = TryGetString(
            dataView.Properties,
            DeskBoxDragData.StackReorderKeyProperty);
        string? sourceStackKey = TryGetString(
            dataView.Properties,
            DeskBoxDragData.SourceStackKeyProperty);
        bool isInternalReorder =
            string.Equals(
                TryGetString(
                    dataView.Properties,
                    "DeskBoxInternalDragToken"),
                "DeskBox.WidgetItemDrag.v2",
                StringComparison.Ordinal) &&
            string.Equals(
                TryGetString(
                    dataView.Properties,
                    "DeskBoxSourceWidgetId"),
                WidgetId,
                StringComparison.Ordinal) &&
            (paths.Length > 0 || !string.IsNullOrWhiteSpace(stackReorderKey));
        bool hasSurfacePathData = paths.Length > 0 ||
            DeskBoxDragData.HasImportableFileData(dataView);

        _dragPayloadSnapshot = new DragPayloadSnapshot(
            dataView,
            paths,
            isInternalReorder,
            hasSurfacePathData,
            stackReorderKey,
            sourceStackKey,
            sourceWidgetId,
            internalDragToken);
        _dragPayloadSessionActive = true;
        _dragDirectoryCache.Clear();
        _dragUnsafeDropCache.Clear();
        return _dragPayloadSnapshot;
    }

    private void ResetDragPayloadCache()
    {
        _dragPayloadSnapshot = null;
        _dragPayloadSessionActive = false;
        _dragDirectoryCache.Clear();
        _dragUnsafeDropCache.Clear();
        _lastDropVisualState = null;
        _stackDropItemsDataView = null;
        _stackDropItemsTargetKey = null;
        _stackDropItemsTargetMemberCount = -1;
        _stackDropItemsCache = [];
    }

    internal void ClearDragSessionVisualState()
    {
        ClearFolderDropTarget();
        ClearStackMemberDropTarget();
        ApplyDropVisual(FileDropVisualState.None);
        PersistSurfaceReorder();
        ResetDragPayloadCache();
    }

    internal bool HasActiveChildDropTargetVisual =>
        _folderDropTarget is not null || _stackMemberDropTarget is not null;

    internal bool SuppressesNativeShellDragVisual =>
        _dragPayloadSnapshot?.IsDeskBoxFileDrag == true;

    /// <summary>
    /// Uses the OLE IDropTarget screen coordinate as a fallback for routed
    /// DragLeave. WinUI can omit or delay a child leave while the pointer moves
    /// quickly or the host is resizing; the native callback still supplies the
    /// current physical point. This path only clears stale state and never
    /// creates a highlight, so routed XAML drag handling remains authoritative.
    /// </summary>
    internal void ObserveNativeDragPointer(
        int screenX,
        int screenY,
        bool hasFileData)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!hasFileData)
        {
            ClearDragSessionVisualState();
            return;
        }

        if (!HasActiveChildDropTargetVisual)
        {
            return;
        }

        if (!IsScreenPointInsideElement(Root, screenX, screenY))
        {
            ClearDragSessionVisualState();
            return;
        }

        if (_folderDropTarget is { } folderTarget &&
            !IsScreenPointInsideElement(folderTarget, screenX, screenY))
        {
            ClearFolderDropTarget();
        }

        if (_stackMemberDropTarget is { } stackTarget &&
            !IsScreenPointInsideElement(stackTarget, screenX, screenY))
        {
            ClearStackMemberDropTarget();
        }
    }

    private bool IsScreenPointInsideElement(
        FrameworkElement element,
        int screenX,
        int screenY)
    {
        if (_hostWindowHandle == IntPtr.Zero ||
            element.Visibility != Visibility.Visible ||
            element.XamlRoot is null ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0 ||
            !Win32Helper.GetWindowRect(
                _hostWindowHandle,
                out Win32Helper.RECT windowBounds))
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point topLeft = element.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = element.XamlRoot.RasterizationScale;
            double left = windowBounds.Left + (topLeft.X * scale);
            double top = windowBounds.Top + (topLeft.Y * scale);
            double right = left + (element.ActualWidth * scale);
            double bottom = top + (element.ActualHeight * scale);
            return screenX >= left &&
                   screenX < right &&
                   screenY >= top &&
                   screenY < bottom;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsSameDragPayload(
        DataPackageView dataView,
        DragPayloadSnapshot cached)
    {
        if (ReferenceEquals(cached.DataView, dataView))
        {
            return true;
        }

        string[] paths = GetPackagePaths(dataView);
        if (!paths.SequenceEqual(
                cached.Paths,
                StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(
                   TryGetString(
                       dataView.Properties,
                       DeskBoxDragData.SourceWidgetIdProperty),
                   cached.SourceWidgetId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   TryGetString(
                       dataView.Properties,
                       DeskBoxDragData.InternalFileDragTokenProperty),
                   cached.InternalDragToken,
                   StringComparison.Ordinal) &&
               string.Equals(
                   TryGetString(
                       dataView.Properties,
                       DeskBoxDragData.StackReorderKeyProperty),
                   cached.StackReorderKey,
                   StringComparison.Ordinal) &&
               string.Equals(
                   TryGetString(
                       dataView.Properties,
                       DeskBoxDragData.SourceStackKeyProperty),
                   cached.SourceStackKey,
                   StringComparison.Ordinal);
    }

    internal bool IsInternalReorderDrag(DataPackageView dataView) =>
        GetDragPayload(dataView).IsInternalReorder;

    private bool TryGetStackPopoverDragItems(
        DragPayloadSnapshot payload,
        out WidgetStackItem sourceStack,
        out WidgetItem[] items)
    {
        sourceStack = null!;
        items = [];
        if (!payload.IsStackPopoverMemberDrag ||
            payload.SourceStackKey is not { Length: > 0 } sourceStackKey ||
            ViewModel.FindStackByKey(sourceStackKey) is not { } currentStack)
        {
            return false;
        }

        var sourcePaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string path in payload.Paths)
        {
            try
            {
                sourcePaths.Add(Path.GetFullPath(path));
            }
            catch (Exception)
            {
                // The internal token is scoped to DeskBox, but still treat
                // malformed package metadata as an invalid membership drag.
            }
        }

        items = currentStack.Members
            .Where(item =>
            {
                try
                {
                    return sourcePaths.Contains(
                        Path.GetFullPath(item.Path));
                }
                catch (Exception)
                {
                    return false;
                }
            })
            .ToArray();
        if (items.Length == 0)
        {
            return false;
        }

        sourceStack = currentStack;
        return true;
    }

    private bool IsPointerInsideRoot(DragEventArgs e)
    {
        if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point point = e.GetPosition(Root);
            return point.X >= 0 &&
                   point.Y >= 0 &&
                   point.X <= Root.ActualWidth &&
                   point.Y <= Root.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private DataPackageOperation ResolveSurfaceDropOperation(
        DataPackageView dataView,
        bool forceCopy = false)
    {
        return ToDataPackageOperation(
            ResolveSurfaceDropIntent(dataView, forceCopy));
    }

    internal static DataPackageOperation ResolveSafeDropCompletionOperation(
        DataPackageOperation requestedOperation,
        bool isDeskBoxFileDrag,
        int requestedMoveCount,
        int completedMoveCount)
    {
        if (requestedOperation != DataPackageOperation.Move)
        {
            return requestedOperation;
        }

        if (requestedMoveCount <= 0 ||
            completedMoveCount != requestedMoveCount)
        {
            return DataPackageOperation.None;
        }

        // DeskBox already performs the filesystem move and explicitly tells the
        // source widget which paths completed. Returning Move to its native Shell
        // data object would ask Shell to clean the source a second time; when the
        // target released the deferral before importing, that cleanup sent the
        // original shortcuts to the Recycle Bin before they could be transferred.
        return isDeskBoxFileDrag
            ? DataPackageOperation.None
            : DataPackageOperation.Move;
    }

    private static async Task<DroppedFileBatch> GetSurfaceDropFilesAsync(
        DataPackageView dataView)
    {
        string[] paths = GetPackagePaths(dataView);
        if (paths.Length > 0)
        {
            // DataPackageView remains on its owning thread. Once its path strings
            // are copied locally, provider/ACL/reparse-point probes run in the
            // background so an unusual source cannot freeze the dispatcher.
            DroppedFilePath[] files = await Task.Run(() => paths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path =>
                    {
                        try
                        {
                            return Path.GetFullPath(path);
                        }
                        catch
                        {
                            return string.Empty;
                        }
                    })
                    .Where(path => File.Exists(path) || Directory.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new DroppedFilePath(
                        path,
                        Path.GetFileName(path),
                        ForceManagedCopy: false))
                    .ToArray())
                .ConfigureAwait(false);
            return new DroppedFileBatch(files, temporaryDirectory: null, skippedCount: 0);
        }

        return await DeskBoxDragData.TryGetDroppedFilesAsync(dataView);
    }

    private async Task<IReadOnlyList<string>> ImportDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> droppedFiles,
        bool? moveWhenMapped,
        FileDropIntent? intentOverride = null)
    {
        EnsureTrackedImportStarted();
        IProgress<FileService.FileTransferProgress> progress =
            new CallbackProgress<FileService.FileTransferProgress>(
                ReportImportProgress);
        var movedSourcePaths = new List<string>();
        int importedItemCount = 0;
        try
        {
            string[] regularPaths = droppedFiles
                .Where(file => !file.ForceManagedCopy)
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (regularPaths.Length > 0)
            {
                if (intentOverride == FileDropIntent.Shortcut)
                {
                    IReadOnlyList<string> created =
                        await CreateShortcutDropAsync(
                            droppedFiles.Where(file => !file.ForceManagedCopy)
                                .ToArray(),
                            ViewModel.CurrentFolderPath ??
                                ViewModel.MappedFolderPath,
                            ActiveImportCancellationToken);
                    importedItemCount += created.Count;
                }
                else
                {
                    IReadOnlyList<string> completed = await ViewModel.ImportPathsAsync(
                        regularPaths,
                        moveWhenMapped,
                        useShellProgress: true,
                        ownerWindowHandle: _hostWindowHandle,
                        progress: progress,
                        cancellationToken: ActiveImportCancellationToken);
                    importedItemCount += completed.Count;
                    if (moveWhenMapped == true)
                    {
                        movedSourcePaths.AddRange(completed);
                    }
                }
            }

            string[] managedCopyPaths = droppedFiles
                .Where(file => file.ForceManagedCopy)
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (managedCopyPaths.Length > 0)
            {
                // Virtual browser files and URL downloads live in a temporary
                // directory owned by DroppedFileBatch. They must always be copied.
                IReadOnlyList<string> completed = await ViewModel.ImportPathsAsync(
                    managedCopyPaths,
                    moveWhenMapped: false,
                    useShellProgress: false,
                    ownerWindowHandle: _hostWindowHandle,
                    progress: progress,
                    cancellationToken: ActiveImportCancellationToken);
                importedItemCount += completed.Count;
            }

            await CompleteTrackedImportAsync(ImportCompletionState.Completed);
            global::DeskBox.App.Current.NotifyOnboardingFileImportCompleted(
                importedItemCount);
            return movedSourcePaths;
        }
        catch (OperationCanceledException)
        {
            await CompleteTrackedImportAsync(ImportCompletionState.Canceled);
            throw;
        }
        catch
        {
            await CompleteTrackedImportAsync(ImportCompletionState.Failed);
            throw;
        }
    }

    /// <summary>
    /// Imports a file payload received by the owning surface window's native
    /// drag-drop bridge. Grouped file content has no HWND of its own, so this
    /// mirrors the regular surface import pipeline after the host extracts the
    /// native OLE or WM_DROPFILES payload.
    /// </summary>
    internal async Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool? copyWhenMapped = null,
        WidgetItem? targetItem = null,
        FileDropIntent? forcedIntent = null)
    {
        if (_isDisposed || _isImportBusy)
        {
            return false;
        }

        DroppedFilePath[] droppedFiles = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DroppedFilePath(
                path,
                Path.GetFileName(path),
                ForceManagedCopy: containsTemporaryFiles))
            .ToArray();
        if (droppedFiles.Length == 0)
        {
            return false;
        }

        if (HasTransferConflict(
                droppedFiles.Select(file => file.Path),
                targetItem?.Path ?? ViewModel.CurrentFolderPath))
        {
            FileTransferPathState targetState =
                GetTransferState(targetItem);
            ShowTransferBlockedFeedback(
                targetState.IsActive
                    ? targetState
                    : _fileService.TransferSessions.GetState(
                        droppedFiles[0].Path));
            return false;
        }

        bool mapped = !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath);
        bool followWindows = string.Equals(
            _settingsService.Settings.ManagedDropAction,
            SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);
        string destinationPath = targetItem is { IsFolder: true }
            ? targetItem.Path
            : ViewModel.CurrentFolderPath ??
                ViewModel.MappedFolderPath ??
                string.Empty;
        bool sameVolume = FileDropIntentPolicy.AreAllOnSameVolume(
            droppedFiles.Select(file => file.Path),
            destinationPath);
        FileDropIntent intent = forcedIntent ??
            (followWindows
                ? FileDropIntentPolicy.ResolveMappedTransfer(
                    hasMappedFolder: mapped,
                    forceCopy: containsTemporaryFiles,
                    controlDown: Win32Helper.IsKeyPressed(VirtualKey.Control),
                    shiftDown: Win32Helper.IsKeyPressed(VirtualKey.Shift),
                    defaultMove: true,
                    followWindows: true,
                    sameVolume: sameVolume)
                : copyWhenMapped switch
                {
                    true => FileDropIntent.Copy,
                    false => FileDropIntent.Move,
                    _ => FileDropIntentPolicy.ResolveMappedTransfer(
                        hasMappedFolder: mapped,
                        forceCopy: containsTemporaryFiles,
                        controlDown: Win32Helper.IsKeyPressed(VirtualKey.Control),
                        shiftDown: Win32Helper.IsKeyPressed(VirtualKey.Shift),
                        defaultMove: string.Equals(
                            _settingsService.Settings.ManagedDropAction,
                            SettingsService.ManagedDropActionMove,
                            StringComparison.Ordinal),
                        altDown: Win32Helper.IsKeyPressed(VirtualKey.Menu),
                        followWindows: false,
                        sameVolume: sameVolume)
                });
        bool? moveWhenMapped = mapped
            ? intent == FileDropIntent.Move
            : null;
        if (targetItem is WidgetStackItem stack)
        {
            return await ImportNativeDroppedFilesIntoStackAsync(
                droppedFiles,
                stack,
                moveWhenMapped,
                intentOverride: intent == FileDropIntent.Shortcut
                    ? FileDropIntent.Shortcut
                    : null);
        }

        if (targetItem is { IsFolder: true, Path.Length: > 0 } folder)
        {
            return await ImportNativeDroppedFilesIntoFolderAsync(
                droppedFiles,
                folder,
                move: intent == FileDropIntent.Move,
                intentOverride: intent == FileDropIntent.Shortcut
                    ? FileDropIntent.Shortcut
                    : null);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string importId = Guid.NewGuid().ToString("N")[..8];
        App.Log(
            $"[Import] Native import start id={importId} widget={WidgetId} " +
            $"count={droppedFiles.Length} move={moveWhenMapped == true} " +
            $"owner=0x{_hostWindowHandle.ToInt64():X}");
        try
        {
            await ImportDroppedFilesAsync(
                droppedFiles,
                moveWhenMapped,
                intentOverride: intent == FileDropIntent.Shortcut
                    ? FileDropIntent.Shortcut
                    : null);
            App.Log(
                $"[Import] Native import completed id={importId} widget={WidgetId} " +
                $"count={droppedFiles.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
            ShowFeedback(new(
                _localizationService.Format(
                    moveWhenMapped == true
                        ? "Widget.MovedCount"
                        : "Widget.PastedCount",
                    droppedFiles.Length),
                WidgetFeedbackSeverity.Success,
                "native-file-drop"));
            return true;
        }
        catch (OperationCanceledException)
        {
            App.Log(
                $"[Import] Native import canceled id={importId} widget={WidgetId} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return false;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Native file drop failed id={WidgetId} " +
                $"import={importId} elapsedMs={stopwatch.ElapsedMilliseconds}: {ex}");
            ShowFeedback(new(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "native-file-drop-error"));
            return false;
        }
        finally
        {
            App.Log(
                $"[Import] Native import finalized id={importId} widget={WidgetId} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
    }

    private void HandleSurfaceRealTimeReorder(
        DragPayloadSnapshot payload,
        Windows.Foundation.Point position)
    {
        string? stackKey = payload.StackReorderKey;
        if (!string.IsNullOrWhiteSpace(stackKey))
        {
            if (!string.Equals(
                    _surfaceReorderStackKey,
                    stackKey,
                    StringComparison.Ordinal))
            {
                _surfaceReorderDraggedItem = null;
                _surfaceReorderPathSet = null;
                _surfaceReorderLastView = null;
            }

            _isSurfaceReorderDragActive = true;
            _surfaceReorderStackKey = stackKey;
            _surfaceReorderPaths = [];
            UpdateSurfaceReorderPreview(position);
            return;
        }

        string[] paths = payload.Paths;
        if (paths.Length == 0)
        {
            return;
        }

        _surfaceReorderPathSet ??= paths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        WidgetItem? draggedItem = _surfaceReorderDraggedItem ??
            ViewModel.Items.FirstOrDefault(item =>
                _surfaceReorderPathSet.Contains(Path.GetFullPath(item.Path)));
        if (draggedItem is null)
        {
            return;
        }

        if (!_isSurfaceReorderDragActive)
        {
            if (ViewModel.UsesStackProjection)
            {
                if (!ViewModel.PrepareVisibleItemReorder(draggedItem))
                {
                    return;
                }
            }
            else if (ViewModel.Config.SortMode != WidgetSortMode.Manual)
            {
                ViewModel.SetSortMode(WidgetSortMode.Manual);
            }

            _isSurfaceReorderDragActive = true;
            _surfaceReorderPaths = paths;
            _surfaceReorderDraggedItem = draggedItem;
        }

        UpdateSurfaceReorderPreview(position);
    }

    private void HandleSurfaceFinalReorder(
        IReadOnlyList<string> paths,
        Windows.Foundation.Point position)
    {
        if (!_isSurfaceReorderDragActive)
        {
            _surfaceReorderPaths = paths.ToArray();
            _isSurfaceReorderDragActive =
                _surfaceReorderPaths.Length > 0;
            _surfaceReorderPathSet = _surfaceReorderPaths
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _surfaceReorderDraggedItem = ViewModel.Items.FirstOrDefault(item =>
                _surfaceReorderPathSet.Contains(Path.GetFullPath(item.Path)));
        }

        CommitSurfaceReorder(position);
    }

    private void UpdateSurfaceReorderPreview(
        Windows.Foundation.Point position)
    {
        ListViewBase activeView = GetActiveItemsView();
        if (_surfaceReorderHasLastPosition &&
            ReferenceEquals(_surfaceReorderLastView, activeView) &&
            Math.Abs(position.X - _surfaceReorderLastPosition.X) < 0.5 &&
            Math.Abs(position.Y - _surfaceReorderLastPosition.Y) < 0.5)
        {
            return;
        }

        _surfaceReorderLastPosition = position;
        _surfaceReorderHasLastPosition = true;
        _surfaceReorderLastView = activeView;
        _surfaceReorderInsertionIndex =
            ReorderDropIndexCalculator.Compute(
                activeView,
                position,
                _surfaceReorderInsertionIndex);
        UpdateSurfaceReorderInsertionIndicator(position);
    }

    private void UpdateSurfaceReorderInsertionIndicator(
        Windows.Foundation.Point position)
    {
        ListViewBase activeView = GetActiveItemsView();
        if (!_isSurfaceReorderDragActive ||
            _surfaceReorderInsertionIndex < 0 ||
            !ReorderDropIndexCalculator.TryGetInsertionIndicatorPlacement(
                activeView,
                SelectionOverlay,
                _surfaceReorderInsertionIndex,
                position,
                out ReorderInsertionIndicatorPlacement placement))
        {
            HideSurfaceReorderInsertionIndicator();
            return;
        }

        bool wasVisible =
            ReorderInsertionIndicator.Visibility == Visibility.Visible;
        ReorderInsertionIndicator.Width = placement.Bounds.Width;
        ReorderInsertionIndicator.Height = placement.Bounds.Height;
        ReorderInsertionLine.Width = placement.IsVertical
            ? 1.5
            : placement.Bounds.Width;
        ReorderInsertionLine.Height = placement.IsVertical
            ? placement.Bounds.Height
            : 1.5;
        if (ReorderInsertionGlow.Background is LinearGradientBrush glowBrush)
        {
            glowBrush.StartPoint = placement.IsVertical
                ? new Windows.Foundation.Point(0, 0.5)
                : new Windows.Foundation.Point(0.5, 0);
            glowBrush.EndPoint = placement.IsVertical
                ? new Windows.Foundation.Point(1, 0.5)
                : new Windows.Foundation.Point(0.5, 1);
        }
        Canvas.SetLeft(
            ReorderInsertionIndicator,
            placement.Bounds.X);
        Canvas.SetTop(
            ReorderInsertionIndicator,
            placement.Bounds.Y);
        ReorderInsertionIndicator.Opacity = 1;
        ReorderInsertionIndicator.Visibility = Visibility.Visible;
        if (!wasVisible)
        {
            ReorderInsertionIndicatorAnimator.Start(
                ReorderInsertionIndicator);
        }
    }

    private void HideSurfaceReorderInsertionIndicator()
    {
        ReorderInsertionIndicatorAnimator.Stop(
            ReorderInsertionIndicator);
        ReorderInsertionIndicator.Visibility = Visibility.Collapsed;
        ReorderInsertionIndicator.Opacity = 0;
        ReorderInsertionIndicator.Width = 0;
        ReorderInsertionIndicator.Height = 0;
    }

    private void ApplySurfaceReorder(
        Windows.Foundation.Point position)
    {
        ListViewBase activeView = GetActiveItemsView();
        int targetIndex = ReorderDropIndexCalculator.Compute(
            activeView,
            position,
            _surfaceReorderInsertionIndex);
        _surfaceReorderInsertionIndex = targetIndex;

        if (!string.IsNullOrWhiteSpace(_surfaceReorderStackKey))
        {
            if (ViewModel.Config.SortMode != WidgetSortMode.Manual)
            {
                ViewModel.SetSortMode(WidgetSortMode.Manual);
            }
            ViewModel.MoveStackForReorder(
                _surfaceReorderStackKey,
                targetIndex);
            return;
        }

        if (_surfaceReorderPaths.Length == 0)
        {
            return;
        }

        _surfaceReorderPathSet ??= _surfaceReorderPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        WidgetItem? draggedItem = _surfaceReorderDraggedItem ??
            ViewModel.Items.FirstOrDefault(item =>
                _surfaceReorderPathSet.Contains(Path.GetFullPath(item.Path)));
        if (draggedItem is null)
        {
            return;
        }

        int currentIndex = ViewModel.UsesStackProjection
            ? activeView.Items.IndexOf(draggedItem)
            : ViewModel.Items.IndexOf(draggedItem);
        if (currentIndex < 0)
        {
            return;
        }

        if (ViewModel.UsesStackProjection)
        {
            if (!draggedItem.IsStackChild &&
                ViewModel.Config.SortMode != WidgetSortMode.Manual)
            {
                ViewModel.SetSortMode(WidgetSortMode.Manual);
            }
            ViewModel.MoveVisibleItemForReorder(
                draggedItem,
                targetIndex);
            return;
        }

        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        if (targetIndex == currentIndex || targetIndex < 0)
        {
            return;
        }

        ViewModel.MoveItemForReorder(
            draggedItem,
            targetIndex);
    }

    private void PersistSurfaceReorder()
    {
        HideSurfaceReorderInsertionIndicator();
        _isSurfaceReorderDragActive = false;
        _surfaceReorderPaths = [];
        _surfaceReorderStackKey = null;
        _surfaceReorderInsertionIndex = -1;
        _surfaceReorderDraggedItem = null;
        _surfaceReorderPathSet = null;
        _surfaceReorderLastView = null;
        _surfaceReorderLastPosition = default;
        _surfaceReorderHasLastPosition = false;
    }

    private void CommitSurfaceReorder(
        Windows.Foundation.Point position)
    {
        if (!_isSurfaceReorderDragActive)
        {
            return;
        }

        ApplySurfaceReorder(position);
        if (string.IsNullOrWhiteSpace(_surfaceReorderStackKey))
        {
            ViewModel.PersistManualOrder();
        }

        PersistSurfaceReorder();
    }

    private void ApplyDropVisual(FileDropVisualState state)
    {
        if (_lastDropVisualState == state)
        {
            return;
        }

        _lastDropVisualState = state;
        // Match the standalone file widget: keep content readable and let the
        // native drag caption communicate the operation and destination type.
        DropOverlay.Visibility = Visibility.Collapsed;
        DropOverlay.Opacity = 0;
        ItemsGrid.Opacity = 1;
        ItemsList.Opacity = 1;
        EmptyState.Opacity = 1;
    }

    private Microsoft.UI.Xaml.Media.Brush? ResolveBrush(string key)
    {
        for (DependencyObject? current = this;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement element &&
                element.Resources.TryGetValue(key, out object? scopedValue) &&
                scopedValue is Microsoft.UI.Xaml.Media.Brush scopedBrush)
            {
                return scopedBrush;
            }
        }

        return Application.Current.Resources.TryGetValue(key, out object? value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (await TryHandleClipboardShortcutAsync(e))
        {
            return;
        }

        if (await TryHandleSpacePreviewAsync(e))
        {
            return;
        }

        if (e.Handled)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            FileItemSelectionGeometry.HasAncestor<TextBox>(source))
        {
            return;
        }

        CoreVirtualKeyStates controlState =
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        bool control = controlState.HasFlag(CoreVirtualKeyStates.Down);
        CoreVirtualKeyStates shiftState =
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        bool shift = shiftState.HasFlag(CoreVirtualKeyStates.Down);
        CoreVirtualKeyStates menuState =
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        bool alt = menuState.HasFlag(CoreVirtualKeyStates.Down);
        if (alt && e.Key == VirtualKey.Up && ViewModel.CanNavigateUp)
        {
            e.Handled = true;
            await NavigateUpFromSurfaceAsync();
            return;
        }

        if (control && e.Key == VirtualKey.A)
        {
            e.Handled = true;
            ListViewBase activeView = GetActiveItemsView();
            activeView.SelectedItems.Clear();
            foreach (WidgetItem item in activeView.Items
                         .OfType<WidgetItem>()
                         .Where(item => item is not WidgetStackItem))
            {
                activeView.SelectedItems.Add(item);
            }
            UpdateSelectionCommandBar();
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (_stackPopoverPopupOpen ||
                _stackPopoverHostWindow?.IsVisible == true)
            {
                CloseStackPopover();
                e.Handled = true;
                return;
            }

            if (ViewModel.HasExpandedStack)
            {
                if (ViewModel.GetExpandedStack() is { } expandedStack)
                {
                    RequestStackState(
                        expandedStack,
                        expanded: false);
                }
                e.Handled = true;
                return;
            }

            if (App.Current.WidgetManager is { } manager)
            {
                _ = manager.CloseQuickLookPreviewAsync();
            }
            e.Handled = true;
            ClearSelection();
            _cutClipboardPaths = [];
            ApplyCutState();
            return;
        }

        if (control && shift && e.Key == VirtualKey.C)
        {
            e.Handled = true;
            CopySelectedPathsToClipboard();
            return;
        }

        if (control && shift && e.Key == VirtualKey.N)
        {
            e.Handled = true;
            await CreateFolderInMappedLocationAsync();
            return;
        }

        if (alt && e.Key == VirtualKey.Enter &&
            GetSelectedItems().FirstOrDefault() is { } propertiesTarget)
        {
            e.Handled = true;
            ShowFileProperties(propertiesTarget);
            return;
        }

        if (shift && e.Key == VirtualKey.F10)
        {
            e.Handled = true;
            ShowKeyboardContextMenu();
            return;
        }

        if (e.Key == VirtualKey.F2 &&
            GetSelectedItems().FirstOrDefault() is { } renameTarget)
        {
            e.Handled = true;
            await RenameItemAsync(renameTarget);
            return;
        }

        if (e.Key == VirtualKey.Delete &&
            GetSelectedItems() is { Count: > 0 } deleteTargets)
        {
            e.Handled = true;
            await DeleteItemsAsync(deleteTargets, permanently: shift);
            return;
        }

        if (e.Key == VirtualKey.Enter &&
            GetSelectedItems().FirstOrDefault() is { } openTarget)
        {
            e.Handled = true;
            bool fromStackPopover =
                ReferenceEquals(sender, _stackPopoverItemsView);
            await ActivateItemAsync(openTarget);
            if (fromStackPopover)
            {
                CloseStackPopover(releaseImmediately: true);
            }
            return;
        }

        if (e.Key == VirtualKey.F5)
        {
            e.Handled = true;
            await RunAsync(RefreshAsync);
        }
    }

    private async void ItemsView_PreviewKeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        ShowScrollBarTemporarily(sender as ListViewBase);
        if (e.Key == VirtualKey.Enter &&
            sender is ListViewBase
            {
                SelectedItem: WidgetStackItem stack
            })
        {
            e.Handled = true;
            ToggleStackFromInput(stack);
            return;
        }

        if (await TryHandleClipboardShortcutAsync(e))
        {
            return;
        }

        if (await TryHandleSpacePreviewAsync(e) || e.Handled)
        {
            return;
        }

        QueueQuickLookBoundaryNavigation(e);
    }

    internal async Task<bool> TryHandleClipboardShortcutAsync(
        KeyRoutedEventArgs e)
    {
        if (e.Handled ||
            e.OriginalSource is DependencyObject source &&
            FileItemSelectionGeometry.HasAncestor<TextBox>(source) ||
            !Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            return false;
        }

        bool shift = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        if (e.Key is VirtualKey.C or VirtualKey.X && !shift)
        {
            e.Handled = true;
            await RunAsync(() => CopySelectionToClipboardAsync(
                cut: e.Key == VirtualKey.X));
            return true;
        }

        if (e.Key == VirtualKey.V)
        {
            e.Handled = true;
            await RunAsync(PasteFromClipboardAsync);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleSpacePreviewAsync(KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space ||
            e.OriginalSource is DependencyObject source &&
            FileItemSelectionGeometry.HasAncestor<TextBox>(source))
        {
            return false;
        }

        IReadOnlyList<WidgetItem> selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0 ||
            selectedItems.Any(item => item is WidgetStackItem))
        {
            return false;
        }

        if (TryBlockTransferOpen(selectedItems[0]))
        {
            e.Handled = true;
            return true;
        }

        // Match the standalone file widget: ListView/GridView handles Space
        // for selection and otherwise swallows the key before normal KeyDown.
        e.Handled = true;
        WidgetItem previewTarget = selectedItems[0];
        if (App.Current.WidgetManager is { } manager)
        {
            await manager.TryToggleQuickLookPreviewAsync(
                this,
                previewTarget.Path);
        }
        else if (s_quickLookService.CanPreview(previewTarget.Path))
        {
            await s_quickLookService.TryToggleAsync(previewTarget.Path);
        }

        return true;
    }

    private ListViewBase GetActiveItemsView()
    {
        if (IsStackPopoverInteractionActive &&
            _stackPopoverItemsView is { } popoverItemsView)
        {
            return popoverItemsView;
        }

        return ViewModel.IconViewVisibility == Visibility.Visible
            ? ItemsGrid
            : ItemsList;
    }

    private IReadOnlyList<WidgetItem> GetSelectedItems()
    {
        return GetActiveItemsView().SelectedItems
            .OfType<WidgetItem>()
            .Where(item => item is not WidgetStackItem)
            .Distinct()
            .ToList();
    }

    internal WidgetItem? GetPrimaryQuickLookSelection()
    {
        IReadOnlyList<WidgetItem> selectedItems = GetSelectedItems();
        return selectedItems.Count == 1 ? selectedItems[0] : null;
    }

    internal IReadOnlyList<string> GetQuickLookNavigationPaths() =>
        GetActiveItemsView().Items
            .OfType<WidgetItem>()
            .Where(item =>
                item is not WidgetStackItem &&
                !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => item.Path)
            .ToArray();

    internal bool TrySelectQuickLookTarget(string path)
    {
        ListViewBase activeView = GetActiveItemsView();
        WidgetItem? target = activeView.Items
            .OfType<WidgetItem>()
            .FirstOrDefault(item =>
                item is not WidgetStackItem &&
                string.Equals(
                    item.Path,
                    path,
                    StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        ClearItemSelection();
        activeView.SelectedItem = target;
        activeView.ScrollIntoView(target);
        return true;
    }

    internal void FocusQuickLookNavigationTarget()
    {
        ListViewBase activeView = GetActiveItemsView();
        activeView.UpdateLayout();
        if (activeView.SelectedItem is { } selected &&
            activeView.ContainerFromItem(selected) is Control container)
        {
            container.Focus(FocusState.Programmatic);
            return;
        }

        activeView.Focus(FocusState.Programmatic);
    }

    private void QueueQuickLookBoundaryNavigation(KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FileItemSelectionGeometry.HasAncestor<TextBox>(source) ||
            e.Key is not (VirtualKey.Left or VirtualKey.Up or
                VirtualKey.Right or VirtualKey.Down) ||
            Win32Helper.IsKeyPressed(VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(VirtualKey.Shift) ||
            Win32Helper.IsKeyPressed(VirtualKey.Menu) ||
            GetPrimaryQuickLookSelection() is not { } selected ||
            App.Current.WidgetManager is not { } manager ||
            !manager.IsCurrentQuickLookPreviewTarget(this, selected.Path))
        {
            return;
        }

        string originalPath = selected.Path;
        VirtualKey key = e.Key;
        DispatcherQueue.TryEnqueue(async () =>
            await manager.ContinueQuickLookNavigationAfterNativeAsync(
                this,
                originalPath,
                key));
    }

    private void Items_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }

        if (sender is ListViewBase listView)
        {
            WidgetStackItem[] selectedStacks = listView.SelectedItems
                .OfType<WidgetStackItem>()
                .Where(stack => stack.IsExpanded)
                .ToArray();
            if (selectedStacks.Length > 0)
            {
                // An expanded stack header is an interaction surface, not a
                // file selection. Keeping one selected during collapse lets
                // WinUI recycle that container onto a member on the next
                // expansion. Collapsed headers remain selectable long enough
                // for the existing stack-reorder drag gesture to start.
                _isSynchronizingSelection = true;
                try
                {
                    foreach (WidgetStackItem stack in selectedStacks)
                    {
                        listView.SelectedItems.Remove(stack);
                    }
                }
                finally
                {
                    _isSynchronizingSelection = false;
                }
            }
        }

        if (e.AddedItems.OfType<WidgetItem>()
            .Any(item => item is not WidgetStackItem))
        {
            ClearOtherWidgetSelections();
            if (GetPrimaryQuickLookSelection() is { } selected)
            {
                _ = App.Current.WidgetManager?
                    .FollowQuickLookSelectionAsync(this, selected.Path);
            }
        }

        RefreshItemSelectionVisuals();
        UpdateSelectionCommandBar();
    }

    private void UpdateSelectionCommandBar()
    {
        SelectionCommandBar.Visibility = Visibility.Collapsed;
    }

    private async void OpenSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems().SingleOrDefault() is { } item)
        {
            await ActivateItemAsync(item);
        }
    }

    private async void CopySelectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => CopySelectionToClipboardAsync(cut: false));
    }

    private async void CutSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => CopySelectionToClipboardAsync(cut: true));
    }

    private async void DeleteSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems() is { Count: > 0 } items)
        {
            await DeleteItemsAsync(items);
        }
    }

    private async void RenameSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems().SingleOrDefault() is { } item)
        {
            await RenameItemAsync(item);
        }
    }

    private static void RestoreSelection(
        ListViewBase view,
        IReadOnlyList<string> selectedPaths)
    {
        view.SelectedItems.Clear();
        foreach (WidgetItem item in view.Items.OfType<WidgetItem>())
        {
            if (selectedPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            {
                view.SelectedItems.Add(item);
            }
        }
    }

    private async Task CopySelectionToClipboardAsync(bool cut)
    {
        if (TryBlockTransferClipboard(GetSelectedItems(), cut))
        {
            return;
        }

        string[] paths = GetSelectedItems()
            .Select(item => item.Path)
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        string clipboardText = string.Join(Environment.NewLine, paths);
        DeskBoxClipboardWriteScope.MarkWrite(
            text: clipboardText,
            paths: paths);
        bool shellClipboardSet =
            ShellClipboardHelper.TrySetFileDropList(paths, cut);
        if (!shellClipboardSet)
        {
            var package = new DataPackage
            {
                RequestedOperation =
                    cut ? DataPackageOperation.Move : DataPackageOperation.Copy
            };
            IReadOnlyList<IStorageItem> storageItems =
                await _fileService.GetStorageItemsAsync(paths);
            if (storageItems.Count > 0)
            {
                package.SetStorageItems(storageItems);
            }
            else
            {
                package.SetText(clipboardText);
            }
            package.Properties["DeskBoxSourceWidgetId"] = WidgetId;
            package.Properties["DeskBoxSourcePaths"] = paths;
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        _cutClipboardPaths = cut ? paths : [];
        ApplyCutState();
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                cut ? "Widget.CutCount" : "Widget.CopyCount",
                paths.Length),
            WidgetFeedbackSeverity.Success,
            cut ? "file-cut" : "file-copy"));
    }

    private async Task PasteFromClipboardAsync()
    {
        if (_isDisposed ||
            _isImportBusy ||
            TryBlockTransferMutation(ViewModel.CurrentFolderPath))
        {
            return;
        }

        DataPackageView? clipboard = TryGetClipboardContent();
        await PasteDataPackageAsync(
            clipboard,
            includeShellFileDropFallback: true);
    }

    private async Task PasteDataPackageAsync(
        DataPackageView? clipboard,
        bool includeShellFileDropFallback)
    {
        if (_isDisposed ||
            _isImportBusy ||
            TryBlockTransferMutation(ViewModel.CurrentFolderPath))
        {
            return;
        }

        string[] sourcePaths = clipboard is null
            ? []
            : GetPackagePaths(clipboard);
        bool move = clipboard?.RequestedOperation.HasFlag(
            DataPackageOperation.Move) == true;

        if (includeShellFileDropFallback &&
            ShellClipboardHelper.TryGetFileDropList(
                out string[] shellPaths,
                out bool shellCut))
        {
            if (sourcePaths.Length == 0)
            {
                sourcePaths = shellPaths;
            }

            move |= shellCut;
        }

        if (sourcePaths.Length == 0 &&
            clipboard?.Contains(StandardDataFormats.StorageItems) == true)
        {
            IReadOnlyList<IStorageItem> storageItems =
                await clipboard.GetStorageItemsAsync();
            sourcePaths = storageItems
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (sourcePaths.Length == 0)
        {
            return;
        }

        IReadOnlyList<string> completedSourcePaths =
            await ImportPathsWithTrackedProgressAsync(
            sourcePaths,
            moveWhenMapped: move);
        if (move &&
            clipboard is not null &&
            TryGetString(clipboard.Properties, "DeskBoxSourceWidgetId")
                is { Length: > 0 } sourceWidgetId &&
            App.Current?.WidgetManager is { } manager)
        {
            await manager.NotifyItemsMovedOutAsync(
                sourceWidgetId,
                completedSourcePaths);
        }

        _cutClipboardPaths = [];
        ApplyCutState();
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                move ? "Widget.MovedCount" : "Widget.PastedCount",
                sourcePaths.Length),
            WidgetFeedbackSeverity.Success,
            move ? "file-move" : "file-paste"));
    }

    private static DataPackageView? TryGetClipboardContent()
    {
        try
        {
            return Clipboard.GetContent();
        }
        catch
        {
            return null;
        }
    }

    private bool CanPasteFromClipboard()
    {
        if (ShellClipboardHelper.HasFileDropList())
        {
            return true;
        }

        DataPackageView? clipboard = TryGetClipboardContent();
        return clipboard is not null &&
            (GetPackagePaths(clipboard).Length > 0 ||
             clipboard.Contains(StandardDataFormats.StorageItems));
    }

    private static string[] GetPackagePaths(DataPackageView package)
    {
        if (!package.Properties.TryGetValue(
                "DeskBoxSourcePaths",
                out object? value))
        {
            return [];
        }

        return value switch
        {
            string[] paths => paths,
            IEnumerable<string> paths => paths.ToArray(),
            _ => []
        };
    }

    private static string? TryGetString(
        DataPackagePropertySetView properties,
        string key)
    {
        return properties.TryGetValue(key, out object? value)
            ? value as string
            : null;
    }

    private async Task DeleteItemsAsync(
        IReadOnlyList<WidgetItem> items,
        bool permanently = false)
    {
        if (items.Count == 0 || TryBlockTransferMutation(items))
        {
            return;
        }

        // Confirmation is delegated to the Windows Shell (per the user's own
        // Explorer confirmation settings): recycle deletes ask "move to the
        // recycle bin?", permanent deletes ask "permanently delete?". No
        // DeskBox dialog is stacked on top.
        FileDeleteBatchResult result = await ViewModel.DeleteItemsAsync(
            items,
            recycle: !permanently,
            ownerHandle: _hostWindowHandle);
        UpdateEmptyState();
        if (result.Failures.Count == 0)
        {
            if (result.DeletedCount == 0)
            {
                // The Shell confirmation was declined for every item; not an
                // outcome worth a feedback toast.
                return;
            }

            ShowFeedback(new WidgetFeedbackRequest(
                _localizationService.Format(
                    permanently
                        ? "Widget.PermanentlyDeletedCount"
                        : "Widget.MovedToRecycleBin",
                    result.DeletedCount),
                WidgetFeedbackSeverity.Success,
                permanently ? "file-permanent-delete" : "file-delete"));
            return;
        }

        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                "Widget.DeletePartialResult",
                result.DeletedCount,
                result.Failures.Count,
                result.Failures[0].Name),
            result.DeletedCount > 0
                ? WidgetFeedbackSeverity.Warning
                : WidgetFeedbackSeverity.Error,
            permanently ? "file-permanent-delete-partial" : "file-delete-partial"));
    }

    private void ApplyCutState()
    {
        foreach (WidgetItem item in ViewModel.Items)
        {
            item.IsCut = _cutClipboardPaths.Contains(
                item.Path,
                StringComparer.OrdinalIgnoreCase);
        }

        UpdateItemSurfaceVisuals();
    }

    private async Task PickAndImportFilesAsync()
    {
        _ = await PickAndImportFilesAsync(suggestedFolder: null);
    }

    private async Task<IReadOnlyList<string>> PickAndImportFilesAsync(
        string? suggestedFolder)
    {
        IReadOnlyList<string> paths =
            await FileOpenPickerService.PickFilesAsync(
                _hostWindowHandle,
                suggestedFolder);
        if (paths.Count > 0)
        {
            await ImportPathsWithTrackedProgressAsync(
                paths,
                moveWhenMapped: null);
        }

        return paths;
    }

    private async Task<IReadOnlyList<string>>
        ImportPathsWithTrackedProgressAsync(
            IEnumerable<string> paths,
            bool? moveWhenMapped)
    {
        if (_isDisposed || _isImportBusy)
        {
            return [];
        }

        DroppedFilePath[] droppedFiles = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DroppedFilePath(
                path,
                Path.GetFileName(path),
                ForceManagedCopy: false))
            .ToArray();
        if (droppedFiles.Length == 0)
        {
            return [];
        }

        BeginTrackedImport();
        try
        {
            return await ImportDroppedFilesAsync(
                droppedFiles,
                moveWhenMapped);
        }
        finally
        {
            if (_activeImportCancellation is not null)
            {
                CancelAndResetTrackedImport();
            }
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            App.Log($"[WidgetSurface] File action canceled id={WidgetId}");
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] File action failed id={WidgetId}: {ex}");
            ShowFeedback(new(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "file-action-error"));
        }
        finally
        {
            UpdateEmptyState();
        }
    }

    private string T(string key) => _localizationService.T(key);

    private void ShowFeedback(WidgetFeedbackRequest request)
    {
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(request));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        PersistSurfaceReorder();
        App.Current.WidgetManager?.NotifyQuickLookSurfaceUnavailable(this);
        StopFolderNavigationVisuals();
        ResetStackInteractionVisuals();
        DisposeStackPopoverLifecycle();
        _isDisposed = true;
        _isReadyForReuse = false;
        _lifetimeCancellation.Cancel();
        CancelAndResetTrackedImport();
        _lifetimeCancellation.Dispose();
        if (_isImportBusy)
        {
            SetImportBusy(false);
        }
        if (_itemRenameTarget is not null)
        {
            CancelItemRename();
        }
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DisposeScrollBarActivityTracking();
        ActualThemeChanged -= FileSurfaceContent_ActualThemeChanged;
        ViewModel.Items.CollectionChanged -= Items_CollectionChanged;
        _fileService.TransferSessions.StateChanged -=
            TransferSessions_StateChanged;
        ResetDragPayloadCache();
        ViewModel.Dispose();
    }
}
