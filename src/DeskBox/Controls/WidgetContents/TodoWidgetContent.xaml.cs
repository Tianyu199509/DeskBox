using System.ComponentModel;
using System.Collections.Specialized;
using System.Numerics;
using DeskBox.Contracts;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent : UserControl, IDisposable
{
    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;
    private sealed record TodoSelectionHitTestItem(
        TodoItemViewModel Item,
        Windows.Foundation.Rect Bounds);

    private const int CopyTapDelayMs = 210;

    private readonly DeskBox.Controls.Todo.TodoRowVisualController _rowVisuals = new();
    private string? _draggedTodoItemId;
    private readonly List<string> _draggedTodoItemIds = [];
    private TodoItemViewModel? _editingItem;
    private TodoItemViewModel? _customDueDateItem;
    private IReadOnlyList<string>? _customDueDateItemIds;
    private TimeSpan _customDueTime = new(9, 0, 0);
    private bool _customDueIncludeTime;
    private bool _isSyncingCustomDueIncludeTime;
    private string? _copySelectionAnchorId;
    private long _undoToastGeneration;
    private long _copyTapGeneration;
    private long _detailTransitionGeneration;
    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private bool _isClosingDetail;
    private bool _isChangingTodoFilter;
    private readonly DispatcherTimer _titleAutosaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(450)
    };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _midnightRolloverTimer;
    private readonly SemaphoreSlim _titleSaveGate = new(1, 1);
    private Button? _pressedColorFilterButton;
    private bool _isStartingColorFilterDrag;
    private bool _colorFilterHandledEventsRegistered;
    // 色点拖拽的进程内载荷：DragStarting 置、Drop 应用时取走、源侧 finally 兜底清。
    // 实例字段依赖待办格子单实例（WidgetManager.CreateSingletonContentFeatureWidgetAsync
    // 存在即复用）；若未来允许多实例，跨格标色会退化为无操作（不崩不脏），届时改 static。
    private string? _draggedColorMarker;
    private bool _isResponsiveLayoutTransitionActive;
    private bool _isInteractiveResizeActive;
    private bool _segmentedLayoutRefreshPending;
    private bool _todoSegmentedRestoreQueued;
    private EventHandler<object>? _todoSegmentedRenderingHandler;
    private int _todoSegmentedStableFrameCount;
    private double _todoSegmentedLastCandidateWidth;
    private DateTimeOffset _suppressColorFilterClickUntil;
    private Windows.Foundation.Point _colorFilterDragStartPoint;
    private Windows.Foundation.Point _selectionStartPoint;
    private Windows.Foundation.Point _selectionCurrentPoint;
    private List<TodoItemViewModel> _selectionSnapshot = [];
    private HashSet<TodoItemViewModel> _selectionPreviewItems = [];
    private List<TodoSelectionHitTestItem> _selectionHitTestItems = [];

    private TextBox TodoEditTextBox => TodoInlineEditor.EditorTextBox;

    private Button TodoEditCancelButton => TodoInlineEditor.CancelButton;

    private Button TodoEditSaveButton => TodoInlineEditor.SaveButton;

    private Button TodoEditCloseButton => TodoInlineEditor.CloseButton;

    private readonly KeyEventHandler _detailTitlePreviewKeyDownHandler;
    private readonly KeyEventHandler _rootPreviewKeyDownHandler;
    private readonly PointerEventHandler _rootPointerPressedHandler;
    private bool _isHostWindowActivated;

    public TodoWidgetContent()
    {
        InitializeComponent();
        if (DragDropPermissionService.IsCurrentProcessElevatedCached())
        {
            // WinUI's StartDragAsync pipeline throws inside an elevated process;
            // disable native ListView item dragging up front so row clicks and
            // marquee selection stay healthy on elevated sessions.
            TodoListView.CanDragItems = false;
        }

        _notesAutosaveTimer.Tick += NotesAutosaveTimer_Tick;
        _titleAutosaveTimer.Tick += TitleAutosaveTimer_Tick;
        _detailTitlePreviewKeyDownHandler = new KeyEventHandler(DetailTitleTextBox_KeyDown);
        _rootPreviewKeyDownHandler = new KeyEventHandler(RootGrid_PreviewKeyDown);
        _rootPointerPressedHandler = new PointerEventHandler(RootGrid_PointerPressed);
        RootGrid.AddHandler(
            UIElement.PreviewKeyDownEvent,
            _rootPreviewKeyDownHandler,
            handledEventsToo: true);
        RootGrid.AddHandler(
            UIElement.PointerPressedEvent,
            _rootPointerPressedHandler,
            handledEventsToo: true);
        GotFocus += TodoWidgetContent_GotFocus;
        LostFocus += TodoWidgetContent_LostFocus;
        Loaded += TodoWidgetContent_Loaded;
        Unloaded += TodoWidgetContent_Unloaded;
        ActualThemeChanged += TodoWidgetContent_ActualThemeChanged;
    }

    private void TodoWidgetContent_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        ApplyEditorVisualStyle();
        ApplySelectionRectangleStyle();
    }

    public TodoWidgetContent(TodoWidgetViewModel viewModel)
        : this()
    {
        ViewModel = viewModel;
    }

    public bool RevealReminderItem(string? itemId, bool preferTodayFilter)
    {
        if (ViewModel is null)
        {
            return false;
        }

        var item = ViewModel.FocusReminderItem(itemId, preferTodayFilter);
        RefreshFilterButtons();
        TodoListView.Focus(FocusState.Programmatic);

        if (item is not null)
        {
            _copySelectionAnchorId = item.Id;
            TodoListView.ScrollIntoView(item);
        }

        return item is not null;
    }

    public TodoWidgetViewModel? ViewModel
    {
        get => DataContext as TodoWidgetViewModel;
        set
        {
            if (DataContext is TodoWidgetViewModel oldViewModel)
            {
                _ = SaveActiveNotesAsync(keepEditing: false);
                _notesAutosaveTimer.Stop();
                _titleAutosaveTimer.Stop();
                _notesEditingItemId = null;
                oldViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                oldViewModel.VisibleItems.CollectionChanged -= VisibleItems_CollectionChanged;
                ClearSubscribedTodoRows();
            }

            DataContext = value;

            if (_isDisposed)
            {
                return;
            }

            QuickAddBar.ViewModel = value;
            OverdueSectionBar.ViewModel = value;

            if (value is not null)
            {
                value.PropertyChanged += ViewModel_PropertyChanged;
                value.VisibleItems.CollectionChanged += VisibleItems_CollectionChanged;
            }

            RefreshFilterButtons();
            _masterPaneWidth = value?.PreferredMasterPaneWidth;
            _detailTitlePreferredHeight = value?.PreferredTitleEditorHeight;
            SynchronizeDetailNotes();
            ApplyMasterDetailLayout(ActualWidth);
            QueueDetailTitleHeightUpdate();
        }
    }

    private void TodoWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.VisibleItems.CollectionChanged -= VisibleItems_CollectionChanged;
            ViewModel.VisibleItems.CollectionChanged += VisibleItems_CollectionChanged;
        }

        App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
        App.Current.LocalizationService.LanguageChanged += OnLanguageChanged;
        ApplyLocalizedText();
        ApplyEditorVisualStyle();
        ApplySelectionRectangleStyle();
        RegisterColorFilterHandledEvents();
        RefreshFilterButtons();
        App.Current.ThemeService.AppearanceChanged -= OnThemeAppearanceChanged;
        App.Current.ThemeService.AppearanceChanged += OnThemeAppearanceChanged;
        ApplySegmentedStyle();
        ApplyMasterDetailLayout(ActualWidth);
        QueueDetailTitleHeightUpdate();
        QueueTodoSegmentedRestore();
        EnsureMidnightRolloverTimerAndRefresh();
        ApplyHostFocusSelectionVisuals();
    }

    private void TodoWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelAllCompletionDwells();
        StopMidnightRolloverTimer();
        CancelTodoSegmentedRestore();
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.VisibleItems.CollectionChanged -= VisibleItems_CollectionChanged;
        }

        App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
        App.Current.ThemeService.AppearanceChanged -= OnThemeAppearanceChanged;
        _draggedTodoItemId = null;
        _draggedTodoItemIds.Clear();
        ResetTodoReorderVisualState();
        _copyTapGeneration++;
        _detailTransitionGeneration++;
        _notesAutosaveTimer.Stop();
        _titleAutosaveTimer.Stop();
        if (!_isDisposed && !_isFlushingTeardown)
        {
            _ = SaveActiveNotesAsync(keepEditing: false);
            _ = FlushDetailTitleAsync(finalizeBlankDraft: false, closeDetailOnFinalize: false);
            CommitOrCloseTodoInlineEdit();
        }
        else
        {
            CloseTodoEdit();
        }

        if (DetailPage is not null)
        {
            DetailPageTransitionHelper.Reset(DetailPage);
        }

        CloseCustomDueDateOverlay();
    }

    private void TodoFilterSegmented_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySegmentedStyle();
        QueueTodoSegmentedRestore();
    }

    private void OnThemeAppearanceChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(OnThemeAppearanceChanged);
            return;
        }

        ApplySegmentedStyle();
    }

    private void TodoFilterSegmented_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplySegmentedLayout();
    }

    internal void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        _isResponsiveLayoutTransitionActive = true;
        CancelTodoSegmentedRestore();
        if (!isCollapsing &&
            double.IsFinite(targetContentWidth) &&
            targetContentWidth >= WidgetSegmentedLayoutHelper.MinimumSafeWidth)
        {
            ApplyMasterDetailLayout(targetContentWidth);
            PrepareTodoSegmentedForExpansion(targetContentWidth);
        }
    }

    internal void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        FinishResponsiveLayoutTransition();
    }

    internal void CancelResponsiveLayoutTransition()
    {
        FinishResponsiveLayoutTransition();
    }

    private void FinishResponsiveLayoutTransition()
    {
        bool shouldRefresh = _segmentedLayoutRefreshPending ||
            TodoFilterSegmented.ActualWidth > 0;
        _isResponsiveLayoutTransitionActive = false;
        _segmentedLayoutRefreshPending = false;
        if (shouldRefresh)
        {
            ApplySegmentedLayout();
        }

        ApplyMasterDetailLayout(ActualWidth);
        QueueTodoSegmentedRestore();
    }

    private void ApplySegmentedLayout(bool allowDuringTransition = false)
    {
        if (_isResponsiveLayoutTransitionActive && !allowDuringTransition)
        {
            _segmentedLayoutRefreshPending = true;
            return;
        }

        if (ViewModel?.TabStyle == SettingsService.WidgetTabStyleButton)
        {
            WidgetSegmentedLayoutHelper.ApplyEqualItemWidths(TodoFilterSegmented);
        }
        else
        {
            WidgetSegmentedLayoutHelper.ApplyNaturalItemWidths(TodoFilterSegmented);
        }
    }

    internal void BeginInteractiveResize()
    {
        _isInteractiveResizeActive = true;
        CancelTodoSegmentedRestore();
    }

    internal void CompleteInteractiveResize(double finalContentWidth)
    {
        _isInteractiveResizeActive = false;
        ApplyMasterDetailLayout(
            double.IsFinite(finalContentWidth) && finalContentWidth > 0
                ? finalContentWidth
                : ActualWidth);
        QueueDetailTitleHeightUpdate();
        QueueTodoSegmentedRestore();
    }

    private void PrepareTodoSegmentedForExpansion(double targetContentWidth)
    {
        if (TodoFilterSegmented is null ||
            ViewModel?.TabBarVisibility != Visibility.Visible ||
            ListHeaderArea.Visibility != Visibility.Visible ||
            !double.IsFinite(targetContentWidth) ||
            targetContentWidth < WidgetSegmentedLayoutHelper.MinimumSafeWidth)
        {
            return;
        }

        // The shell has already frozen its presenter at targetContentWidth.
        // It is therefore safe for Toolkit's EqualPanel to realize the real
        // tabs before the first expansion frame. Keep the delayed restore only
        // for first-load or other layouts that do not yet own a safe slot.
        CancelTodoSegmentedRestore();
        TodoFilterSegmented.Visibility = Visibility.Visible;
        WidgetSegmentedStyleHelper.Apply(TodoFilterSegmented, ViewModel?.TabStyle);
        ApplySegmentedLayout(allowDuringTransition: true);
        _segmentedLayoutRefreshPending = false;
    }

    private void ApplySegmentedStyle()
    {
        if (TodoFilterSegmented is null)
        {
            return;
        }

        WidgetSegmentedStyleHelper.Apply(TodoFilterSegmented, ViewModel?.TabStyle);
        ApplySegmentedLayout();
    }

    // CommunityToolkit's Segmented uses EqualPanel internally. During the
    // widget's first arrange pass (and while the host is resizing), WinUI can
    // provide a transient zero-width rectangle. EqualPanel cannot arrange its
    // spacing into that rectangle. Keep the control out of that unsafe pass
    // and reveal it once the master pane has a real layout slot.
    private void SuspendTodoSegmented()
    {
        CancelTodoSegmentedRestore();
        if (TodoFilterSegmented is not null)
        {
            TodoFilterSegmented.Visibility = Visibility.Collapsed;
        }
    }

    private void QueueTodoSegmentedRestore()
    {
        if (!IsLoaded ||
            TodoFilterSegmented is null ||
            ViewModel?.TabBarVisibility != Visibility.Visible ||
            ListHeaderArea.Visibility != Visibility.Visible ||
            TodoFilterSegmented.Visibility == Visibility.Visible ||
            _todoSegmentedRestoreQueued)
        {
            return;
        }

        _todoSegmentedRestoreQueued = true;
        _todoSegmentedStableFrameCount = 0;
        _todoSegmentedLastCandidateWidth = 0;
        _todoSegmentedRenderingHandler = TodoSegmentedRestore_Rendering;
        CompositionTarget.Rendering += _todoSegmentedRenderingHandler;
    }

    private void TodoSegmentedRestore_Rendering(object? sender, object e)
    {
        if (!IsLoaded ||
            ViewModel?.TabBarVisibility != Visibility.Visible ||
            ListHeaderArea.Visibility != Visibility.Visible)
        {
            // CompositionTarget is a static event. Leaving the callback attached
            // after this view is detached keeps the whole Todo XAML tree alive
            // and continues invoking it on every frame.
            CancelTodoSegmentedRestore();
            return;
        }

        if (_isResponsiveLayoutTransitionActive)
        {
            _todoSegmentedStableFrameCount = 0;
            return;
        }

        double candidateWidth = Math.Min(ListHeaderArea.ActualWidth, RootGrid.ActualWidth);
        if (!double.IsFinite(candidateWidth) ||
            candidateWidth < WidgetSegmentedLayoutHelper.MinimumSafeWidth)
        {
            _todoSegmentedStableFrameCount = 0;
            _todoSegmentedLastCandidateWidth = candidateWidth;
            return;
        }

        if (Math.Abs(candidateWidth - _todoSegmentedLastCandidateWidth) > 0.5)
        {
            _todoSegmentedLastCandidateWidth = candidateWidth;
            _todoSegmentedStableFrameCount = 1;
            return;
        }

        if (++_todoSegmentedStableFrameCount < 3)
        {
            return;
        }

        CancelTodoSegmentedRestore();
        TodoFilterSegmented.Visibility = Visibility.Visible;
        ApplySegmentedStyle();
    }

    private void CancelTodoSegmentedRestore()
    {
        if (_todoSegmentedRenderingHandler is not null)
        {
            CompositionTarget.Rendering -= _todoSegmentedRenderingHandler;
            _todoSegmentedRenderingHandler = null;
        }

        _todoSegmentedRestoreQueued = false;
        _todoSegmentedStableFrameCount = 0;
        _todoSegmentedLastCandidateWidth = 0;
    }

    internal void ReleaseTransientRenderingSubscriptions()
    {
        StopMidnightRolloverTimer();
        CancelTodoSegmentedRestore();
    }

    internal void EnsureMidnightRolloverTimerAndRefresh()
    {
        if (_isDisposed)
        {
            return;
        }

        ViewModel?.RefreshIfDateChanged();
        if (!IsLoaded)
        {
            return;
        }

        ScheduleMidnightRolloverTimer();
    }

    private void ScheduleMidnightRolloverTimer()
    {
        if (_isDisposed || DispatcherQueue is not { } dispatcherQueue)
        {
            return;
        }

        if (_midnightRolloverTimer is null)
        {
            _midnightRolloverTimer = dispatcherQueue.CreateTimer();
            _midnightRolloverTimer.IsRepeating = false;
            _midnightRolloverTimer.Tick += MidnightRolloverTimer_Tick;
        }

        DateTime now = DateTime.Now;
        TimeSpan untilNextMidnight = now.Date.AddDays(1).AddSeconds(1) - now;
        if (untilNextMidnight < TimeSpan.FromSeconds(1))
        {
            untilNextMidnight = TimeSpan.FromSeconds(1);
        }
        else if (untilNextMidnight > TimeSpan.FromHours(6))
        {
            untilNextMidnight = TimeSpan.FromHours(6);
        }

        _midnightRolloverTimer.Stop();
        _midnightRolloverTimer.Interval = untilNextMidnight;
        _midnightRolloverTimer.Start();
    }

    private void MidnightRolloverTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed || !IsLoaded)
        {
            StopMidnightRolloverTimer();
            return;
        }

        ViewModel?.RefreshIfDateChanged();
        ScheduleMidnightRolloverTimer();
    }

    private void StopMidnightRolloverTimer()
    {
        _midnightRolloverTimer?.Stop();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoWidgetViewModel.SelectedFilter))
        {
            ClearCopySelection();
            ViewModel?.CollapseAllExpanded();
            RefreshFilterButtons();
            EnsureWideDetailSelection();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.TabStyle))
        {
            ApplySegmentedStyle();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.TabBarVisibility))
        {
            if (ViewModel?.TabBarVisibility == Visibility.Visible)
            {
                QueueTodoSegmentedRestore();
            }
            else
            {
                SuspendTodoSegmented();
            }
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.VisibleTabCount))
        {
            ApplySegmentedLayout();
            RefreshFilterButtons();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.SelectedColorFilter))
        {
            ClearCopySelection();
            RefreshFilterButtons();
            EnsureWideDetailSelection();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.UndoText) &&
            ViewModel is { CanUndoLastAction: true })
        {
            ShowUndoToast(ViewModel.UndoText, ViewModel.UndoActionText, clearUndoOnHide: false);
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.IsDetailPageOpen))
        {
            SynchronizeDetailNotes();
            ApplyMasterDetailVisibility();
            if (ViewModel?.IsDetailPageOpen == true && !_isDualPane)
            {
                QueueDetailEnterAnimation();
            }
            else if (_isDualPane && ViewModel?.IsDetailPageOpen != true)
            {
                EnsureWideDetailSelection();
            }
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.SelectedDetailItem))
        {
            _titleAutosaveTimer.Stop();
            QueueDetailTitleHeightUpdate();
        }

        if (e.PropertyName is nameof(TodoWidgetViewModel.LayoutPreference) or
            nameof(TodoWidgetViewModel.UseWideDetailPane) or
            nameof(TodoWidgetViewModel.AutoSelectFirstInWideLayout))
        {
            ApplyMasterDetailLayout(ActualWidth);
        }
    }

    private void VisibleItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isDualPane)
        {
            DispatcherQueue.TryEnqueue(EnsureWideDetailSelection);
        }
    }

    private void QueueDetailEnterAnimation()
    {
        if (!EnsureDetailPageRealized())
        {
            return;
        }

        long generation = ++_detailTransitionGeneration;
        DetailPage.IsHitTestVisible = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _detailTransitionGeneration ||
                ViewModel?.IsDetailPageOpen != true ||
                DetailPage.Visibility != Visibility.Visible)
            {
                return;
            }

            DetailPageTransitionHelper.PlayEnter(DetailPage);
        });
    }

    private void OnLanguageChanged()
    {
        ApplyLocalizedText();
    }

    private void ApplyLocalizedText()
    {
        var localization = App.Current.LocalizationService;
        if (DetailNotesEditor is not null)
        {
            DetailNotesEditor.TextResolver = localization.T;
        }

        if (TodoInlineEditor is not null)
        {
            TodoInlineEditor.Title = localization.T("Todo.Menu.Edit");
            TodoInlineEditor.CancelText = localization.T("Common.Cancel");
            TodoInlineEditor.SaveText = localization.T("Common.Save");
        }

        ApplyCustomDueDateLocalization(localization);
    }

    private void ApplyCustomDueDateLocalization(LocalizationService localization)
    {
        if (CustomDueDateOverlay is null)
        {
            return;
        }

        if (CustomDueDateTitleText is not null)
        {
            CustomDueDateTitleText.Text = localization.T("Todo.Due.Custom");
        }

        if (CustomDueDatePicker is not null)
        {
            CustomDueDatePicker.PlaceholderText = localization.T("Todo.Due.Custom");
        }

        if (CustomDueDateCancelButton is not null)
        {
            CustomDueDateCancelButton.Content = localization.T("Common.Cancel");
        }

        if (CustomDueDateSaveButton is not null)
        {
            CustomDueDateSaveButton.Content = localization.T("Common.Save");
        }

        if (CustomDueIncludeTimeLabel is not null)
        {
            CustomDueIncludeTimeLabel.Text = localization.T("Todo.Due.IncludeTime");
            CustomDueIncludeTimeLabel.FontSize = ViewModel?.FilterTextSize ?? 12d;
        }
    }

    public void FocusQuickAdd() => _ = FocusQuickAddAsync();

    private async Task FocusQuickAddAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!_isDualPane && ViewModel.IsDetailPageOpen)
        {
            await CloseDetailAsync();
        }

        ClearCopySelection();
        CloseCustomDueDateOverlay();
        CloseTodoEdit();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            if (ViewModel?.VisibleItems.Count > 0)
            {
                TodoListView.ScrollIntoView(ViewModel.VisibleItems[0]);
            }

            QuickAddBar.FocusInput();
        });
    }

    public void OpenAddEditor() => _ = OpenAddEditorAsync();

    private async Task OpenAddEditorAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!await PrepareForDetailSelectionChangeAsync(nextItemId: null))
        {
            return;
        }

        ClearCopySelection();
        CloseCustomDueDateOverlay();
        CloseTodoEdit();
        QuickAddBar.CloseComposer(clearDraft: false);
        MarkDetailSelectionExplicit();
        ViewModel.OpenNewDetail();
        _ = EnsureDetailPageRealized();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (DetailTitleTextBox is not null)
            {
                DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "todo-detail-add");
                DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(DetailTitleTextBox);
                DetailTitleTextBox.Focus(FocusState.Programmatic);
                DetailTitleTextBox.SelectAll();
            }
        });
    }

    private async Task OpenAddEditorFromDraftAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!await PrepareForDetailSelectionChangeAsync(nextItemId: null))
        {
            return;
        }

        ClearCopySelection();
        CloseCustomDueDateOverlay();
        CloseTodoEdit();
        MarkDetailSelectionExplicit();
        ViewModel.OpenNewDetailFromDraft();
        _ = EnsureDetailPageRealized();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (DetailTitleTextBox is not null)
            {
                DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "todo-detail-draft");
                DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(DetailTitleTextBox);
                DetailTitleTextBox.Focus(FocusState.Programmatic);
                int caret = DetailTitleTextBox.Text?.Length ?? 0;
                DetailTitleTextBox.Select(caret, 0);
            }
        });
    }

    private void AddCard_Click(object sender, RoutedEventArgs e)
    {
        OpenAddEditor();
    }

    private async void QuickAddBar_SubmitRequested(object? sender, EventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        TodoItemViewModel? created = await ViewModel.AddInputAsync();
        if (created is not null)
        {
            TodoListView.ScrollIntoView(created);
            ShowTodoStatus("Todo.Status.Saved");
        }

        QuickAddBar.FocusInput();
    }

    private void QuickAddBar_ExpandRequested(object? sender, EventArgs e)
    {
        _ = OpenAddEditorFromDraftAsync();
    }

    private void QuickAddBar_EscapeUnfocusRequested(object? sender, EventArgs e)
    {
        Focus(FocusState.Programmatic);
    }

    private void OverdueSectionBar_ToggleExpandRequested(object? sender, EventArgs e)
    {
        ViewModel?.ToggleTodayOverdueExpanded();
    }

    private async void OverdueSectionBar_RescheduleRequested(object? sender, EventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RescheduleOverdueToTodayAsync();
    }

    private void QuickAddBar_DueDateFlyoutRequested(object? sender, FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        MenuFlyout flyout = CreateDraftDueDateFlyout();
        flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    private void QuickAddBar_RecurrenceFlyoutRequested(object? sender, FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        MenuFlyout flyout = CreateDraftRecurrenceFlyout();
        flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    private MenuFlyout CreateDraftRecurrenceFlyout()
    {
        var flyout = new MenuFlyout();
        var localization = App.Current.LocalizationService;
        string activeMode = TodoRecurrenceMode.Normalize(ViewModel?.DraftRecurrenceMode);
        foreach (string recurrenceMode in TodoRecurrenceMode.SupportedModes)
        {
            string normalizedMode = TodoRecurrenceMode.Normalize(recurrenceMode);
            bool isSelected = string.Equals(activeMode, normalizedMode, StringComparison.Ordinal);
            var item = new MenuFlyoutItem
            {
                Text = localization.T(TodoRecurrenceService.GetLocalizationKey(normalizedMode)),
                Icon = isSelected ? new FontIcon { Glyph = "\uE73E" } : null
            };
            item.Click += (_, _) =>
            {
                ViewModel?.SetDraftRecurrenceMode(normalizedMode);
                QuickAddBar.FocusInput();
            };
            flyout.Items.Add(item);
        }

        return flyout;
    }

    private MenuFlyout CreateDraftDueDateFlyout()
    {
        var flyout = new MenuFlyout();
        var localization = App.Current.LocalizationService;

        flyout.Items.Add(CreateDraftDuePresetItem(TodoDuePreset.Today, localization.T("Todo.Due.Today")));
        flyout.Items.Add(CreateDraftDuePresetItem(TodoDuePreset.Tomorrow, localization.T("Todo.Due.Tomorrow")));
        flyout.Items.Add(CreateDraftDuePresetItem(TodoDuePreset.ThisWeek, localization.T("Todo.Due.ThisWeek")));
        flyout.Items.Add(CreateDraftDuePresetItem(TodoDuePreset.NextMonday, localization.T("Todo.Due.NextMonday")));
        flyout.Items.Add(new MenuFlyoutSeparator());

        var customItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Due.Custom"),
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        customItem.Click += async (_, _) => await PickCustomDueDateAsync(item: null);
        flyout.Items.Add(customItem);

        if (ViewModel?.DraftDueDate is not null)
        {
            var clearItem = new MenuFlyoutItem
            {
                Text = localization.T("Todo.Due.Clear"),
                Icon = new FontIcon { Glyph = "\uE711" }
            };
            clearItem.Click += (_, _) =>
            {
                ViewModel?.SetDraftDueDatePreset(TodoDuePreset.Clear);
                QuickAddBar.FocusInput();
            };
            flyout.Items.Add(clearItem);
        }

        return flyout;
    }

    private MenuFlyoutItem CreateDraftDuePresetItem(TodoDuePreset preset, string text)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) =>
        {
            ViewModel?.SetDraftDueDatePreset(preset);
            QuickAddBar.FocusInput();
        };
        return item;
    }

    private async void TodoFilterSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isChangingTodoFilter || ViewModel is null)
        {
            return;
        }

        TodoFilter filter = GetSelectedSegmentFilter();
        if (ViewModel.SelectedFilter == filter)
        {
            RefreshFilterButtons();
            return;
        }

        _isChangingTodoFilter = true;
        try
        {
            _titleAutosaveTimer.Stop();
            if (ViewModel.IsCreatingDetailItem)
            {
                if (!await SaveActiveNotesAsync(keepEditing: false))
                {
                    RefreshFilterButtons();
                    return;
                }

                string? draftTitle = DetailTitleTextBox?.Text ?? ViewModel.SelectedDetailItem?.EditText;
                bool hasTitle = !string.IsNullOrWhiteSpace(draftTitle);
                TodoItemViewModel? finalized = await ViewModel.FinalizeDetailAsync(
                    draftTitle,
                    closeDetail: true);
                if (hasTitle && finalized is null)
                {
                    RefreshFilterButtons();
                    return;
                }

                if (finalized is not null)
                {
                    ShowTodoStatus("Todo.Status.Saved");
                }
            }
            else if (ViewModel.SelectedDetailItem is not null)
            {
                await FlushDetailTitleAsync(finalizeBlankDraft: false, closeDetailOnFinalize: false);
            }

            SelectFilter(filter);
        }
        finally
        {
            _isChangingTodoFilter = false;
        }

        EnsureWideDetailSelection();
    }

    private void MetadataDueDate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        var flyout = CreateDueDateFlyout(item);
        flyout.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    private MenuFlyout CreateDueDateFlyout(TodoItemViewModel item)
    {
        var flyout = new MenuFlyout();
        var localization = App.Current.LocalizationService;

        flyout.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Today, localization.T("Todo.Due.Today")));
        flyout.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Tomorrow, localization.T("Todo.Due.Tomorrow")));
        flyout.Items.Add(CreateDuePresetItem(item, TodoDuePreset.ThisWeek, localization.T("Todo.Due.ThisWeek")));
        flyout.Items.Add(CreateDuePresetItem(item, TodoDuePreset.NextMonday, localization.T("Todo.Due.NextMonday")));
        flyout.Items.Add(new MenuFlyoutSeparator());

        var customItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Due.Custom"),
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        customItem.Click += async (_, _) => await PickCustomDueDateAsync(item);
        flyout.Items.Add(customItem);

        if (item.DueDate is not null)
        {
            var clearItem = new MenuFlyoutItem
            {
                Text = localization.T("Todo.Due.Clear"),
                Icon = new FontIcon { Glyph = "\uE711" }
            };
            clearItem.Click += async (_, _) =>
            {
                if (ViewModel is not null)
                {
                    await ViewModel.SetDueDatePresetAsync(item.Id, TodoDuePreset.Clear);
                }
            };
            flyout.Items.Add(clearItem);
        }

        return flyout;
    }

    private void MetadataReminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateReminderOffsetItem(item, null));
        flyout.Items.Add(CreateReminderOffsetItem(item, TodoReminderOptions.ReminderOff));
        flyout.Items.Add(new MenuFlyoutSeparator());
        foreach (int offsetMinutes in TodoReminderOptions.SupportedOffsetMinutes)
        {
            flyout.Items.Add(CreateReminderOffsetItem(item, offsetMinutes));
        }

        flyout.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    private void MetadataRecurrence_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (string recurrenceMode in TodoRecurrenceMode.SupportedModes)
        {
            flyout.Items.Add(CreateRecurrenceItem(item, recurrenceMode));
        }

        flyout.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    private void MetadataColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateColorMarkerItem(item, null));
        flyout.Items.Add(new MenuFlyoutSeparator());
        foreach (string colorMarker in TodoItem.SupportedColorMarkers)
        {
            flyout.Items.Add(CreateColorMarkerItem(item, colorMarker));
        }

        flyout.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    private async void DetailBackButton_Click(object sender, RoutedEventArgs e)
    {
        await CloseDetailAsync();
    }

    private async Task CloseDetailAsync()
    {
        if (_isClosingDetail || ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        _isClosingDetail = true;
        try
        {
            _titleAutosaveTimer.Stop();
            if (!await SaveActiveNotesAsync(keepEditing: false))
            {
                return;
            }

            TodoItemViewModel? finalizedItem = await FlushDetailTitleAsync(
                finalizeBlankDraft: true,
                closeDetailOnFinalize: false);
            if (_isDualPane)
            {
                ApplyMasterDetailVisibility();
                if (finalizedItem is not null)
                {
                    TodoListView.ScrollIntoView(finalizedItem);
                }

                return;
            }

            if (!await PlayDetailExitAnimationAsync(item))
            {
                return;
            }

            ViewModel.CloseDetail();
            ClearTodoListContainerSelection();
            TodoItemViewModel? focusTarget = finalizedItem ?? item;
            if (focusTarget is not null && ViewModel.VisibleItems.Contains(focusTarget))
            {
                ClearCopySelection();
                focusTarget.IsCopySelected = true;
                _copySelectionAnchorId = focusTarget.Id;
            }

            TodoListView.Focus(FocusState.Programmatic);
            ApplyHostFocusSelectionVisuals();
            if (finalizedItem is not null)
            {
                TodoListView.ScrollIntoView(finalizedItem);
            }
        }
        finally
        {
            ResetDetailTransition();
        }
    }

    private async Task<bool> PlayDetailExitAnimationAsync(TodoItemViewModel expectedItem)
    {
        if (DetailPage is null)
        {
            return ReferenceEquals(ViewModel?.SelectedDetailItem, expectedItem);
        }

        long generation = ++_detailTransitionGeneration;
        DetailPage.IsHitTestVisible = false;
        await DetailPageTransitionHelper.PlayExitAsync(DetailPage);
        return generation == _detailTransitionGeneration &&
               ReferenceEquals(ViewModel?.SelectedDetailItem, expectedItem);
    }

    private void ResetDetailTransition()
    {
        if (DetailPage is not null)
        {
            DetailPageTransitionHelper.Reset(DetailPage);
            DetailPage.IsHitTestVisible = true;
        }

        _isClosingDetail = false;
    }

    private async Task<bool> SaveDetailEditorsAsync(TodoItemViewModel item)
    {
        if (ViewModel is null)
        {
            return false;
        }

        if (!await SaveActiveNotesAsync(keepEditing: false))
        {
            return false;
        }

        _titleAutosaveTimer.Stop();
        if (ViewModel.IsCreatingDetailItem || DetailTitleTextBox is null)
        {
            return true;
        }

        return await ViewModel.UpdateItemTextAsync(item.Id, DetailTitleTextBox.Text);
    }

    private void ScheduleDetailTitleAutoSave()
    {
        if (_isDisposed ||
            _isFlushingTeardown ||
            DetailTitleTextBox is null ||
            ViewModel?.SelectedDetailItem is not { } item)
        {
            _titleAutosaveTimer.Stop();
            return;
        }

        string trimmed = (DetailTitleTextBox.Text ?? string.Empty).Trim();
        if (ViewModel.IsCreatingDetailItem)
        {
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                _titleAutosaveTimer.Stop();
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(trimmed) ||
                 string.Equals(item.Text, trimmed, StringComparison.Ordinal))
        {
            _titleAutosaveTimer.Stop();
            return;
        }

        _titleAutosaveTimer.Stop();
        _titleAutosaveTimer.Start();
    }

    private async void TitleAutosaveTimer_Tick(object? sender, object e)
    {
        _titleAutosaveTimer.Stop();
        await FlushDetailTitleAsync(finalizeBlankDraft: false, closeDetailOnFinalize: false);
    }

    private async Task<TodoItemViewModel?> FlushDetailTitleAsync(
        bool finalizeBlankDraft = false,
        bool closeDetailOnFinalize = false)
    {
        _titleAutosaveTimer.Stop();
        if (_isDisposed || ViewModel?.SelectedDetailItem is not { } currentItem)
        {
            return null;
        }

        await _titleSaveGate.WaitAsync();
        try
        {
            if (_isDisposed ||
                ViewModel?.SelectedDetailItem is not { } item ||
                !ReferenceEquals(item, currentItem))
            {
                return null;
            }

            string currentText = DetailTitleTextBox?.Text ?? item.EditText ?? string.Empty;
            string trimmed = currentText.Trim();

            if (ViewModel.IsCreatingDetailItem)
            {
                if (string.IsNullOrWhiteSpace(trimmed) && !finalizeBlankDraft)
                {
                    return null;
                }

                TodoItemViewModel? finalized = await ViewModel.FinalizeDetailAsync(
                    currentText,
                    closeDetail: closeDetailOnFinalize);
                if (finalized is not null)
                {
                    ApplyMasterDetailVisibility();
                }

                return finalized;
            }

            if (!string.IsNullOrWhiteSpace(trimmed) &&
                !string.Equals(item.Text, trimmed, StringComparison.Ordinal))
            {
                await ViewModel.UpdateItemTextAsync(item.Id, currentText);
            }

            if (closeDetailOnFinalize)
            {
                item.CancelEdit();
                ViewModel.CloseDetail();
            }

            return item;
        }
        finally
        {
            _titleSaveGate.Release();
        }
    }

    private async void DetailImportantButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        await SetImportantWithFeedbackAsync(item, !item.IsImportant);
    }

    private async void DetailTitleTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await FlushDetailTitleAsync(finalizeBlankDraft: false, closeDetailOnFinalize: false);
    }

    private async void DetailTitleTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            await CloseDetailAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (ShouldSubmitTodoEditor(e))
            {
                await CloseDetailAsync();
            }
            else
            {
                TextBoxEditorShortcutHelper.InsertLineBreak(DetailTitleTextBox);
            }
        }
    }

    private static bool ShouldSubmitTodoEditor(KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return false;
        }

        bool controlPressed = Win32Helper.IsKeyPressed(VirtualKey.Control);
        return SettingsService.ShouldSubmitEditorOnEnter(
            App.Current.SettingsService.Settings.TodoEditorEnterBehavior,
            controlPressed);
    }

    private bool _isDisposed;
    private bool _isFlushingTeardown;

    internal void FlushPendingEditsBeforeTeardown()
    {
        if (_isDisposed || _isFlushingTeardown)
        {
            return;
        }

        _isFlushingTeardown = true;
        try
        {
            CommitOrCloseTodoInlineEdit();
            if (ViewModel is not { } vm)
            {
                return;
            }

            if (vm.SelectedDetailItem is { } detailItem)
            {
                if (vm.IsCreatingDetailItem)
                {
                    _ = vm.FinalizeDetailAsync(DetailTitleTextBox?.Text, closeDetail: false);
                }
                else if (DetailTitleTextBox is not null &&
                         !string.IsNullOrWhiteSpace(DetailTitleTextBox.Text) &&
                         !string.Equals(detailItem.Text, DetailTitleTextBox.Text.Trim(), StringComparison.Ordinal))
                {
                    _ = vm.UpdateItemTextAsync(detailItem.Id, DetailTitleTextBox.Text);
                }
            }

            if (vm.SelectedDetailItem is { } persistedDetailItem &&
                DetailStepsItemsControl is not null)
            {
                int stepCount = DetailStepsItemsControl.Items.Count;
                for (int index = 0; index < stepCount; index++)
                {
                    if (DetailStepsItemsControl.ContainerFromIndex(index) is DependencyObject container &&
                        FindVisualChild<TextBox>(container, "DetailStepTextBox") is { } stepTextBox &&
                        stepTextBox.DataContext is TodoStepViewModel step &&
                        !string.IsNullOrWhiteSpace(stepTextBox.Text) &&
                        !string.Equals(step.Text, stepTextBox.Text.Trim(), StringComparison.Ordinal))
                    {
                        _ = vm.UpdateStepTextAsync(persistedDetailItem.Id, step.Id, stepTextBox.Text);
                    }
                }
            }

            _titleAutosaveTimer.Stop();
            _notesAutosaveTimer.Stop();
            if (_notesEditingItemId is not null)
            {
                _ = SaveActiveNotesAsync(keepEditing: false);
            }
        }
        finally
        {
            _isFlushingTeardown = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        FlushPendingEditsBeforeTeardown();
        CancelAllCompletionDwells();
        _isDisposed = true;
        ReleaseTransientRenderingSubscriptions();
        ClearSubscribedTodoRows();
        UnregisterColorFilterHandledEvents();
        RootGrid.RemoveHandler(
            UIElement.PreviewKeyDownEvent,
            _rootPreviewKeyDownHandler);
        RootGrid.RemoveHandler(
            UIElement.PointerPressedEvent,
            _rootPointerPressedHandler);
        GotFocus -= TodoWidgetContent_GotFocus;
        LostFocus -= TodoWidgetContent_LostFocus;
        if (DetailTitleTextBox is not null)
        {
            DetailTitleTextBox.RemoveHandler(
                UIElement.PreviewKeyDownEvent,
                _detailTitlePreviewKeyDownHandler);
        }
        ActualThemeChanged -= TodoWidgetContent_ActualThemeChanged;
        _titleAutosaveTimer.Stop();
        _titleAutosaveTimer.Tick -= TitleAutosaveTimer_Tick;
        _notesAutosaveTimer.Stop();
        _notesAutosaveTimer.Tick -= NotesAutosaveTimer_Tick;
        if (_midnightRolloverTimer is not null)
        {
            _midnightRolloverTimer.Stop();
            _midnightRolloverTimer.Tick -= MidnightRolloverTimer_Tick;
            _midnightRolloverTimer = null;
        }
        Loaded -= TodoWidgetContent_Loaded;
        Unloaded -= TodoWidgetContent_Unloaded;
        if (App.Current?.LocalizationService is { } localizationService)
        {
            localizationService.LanguageChanged -= OnLanguageChanged;
        }
        if (App.Current?.ThemeService is { } themeService)
        {
            themeService.AppearanceChanged -= OnThemeAppearanceChanged;
        }
        ViewModel = null;
        QuickAddBar.ReleaseVisualHandlers();
        OverdueSectionBar.ReleaseVisualHandlers();
        TodoListView.ItemsSource = null;
        if (TodoInlineEditor is not null)
        {
            TodoInlineEditor.ReleaseVisualHandlers();
        }
        ReleaseDetailNotesAndStepsVisualHandlers();
        RootGrid.DataContext = null;
        Content = null;
    }

    private async void DetailDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        if (!await SaveDetailEditorsAsync(item))
        {
            return;
        }

        await DeleteItemAsync(item);
    }

    private async void DetailConvertToQuickCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        if (!await SaveDetailEditorsAsync(item))
        {
            return;
        }

        TodoItemViewModel target = ViewModel.SelectedDetailItem ?? item;
        await ConvertTodoItemsToQuickCaptureWithFeedbackAsync([target]);
    }

    private void DetailEditorInput_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "todo-detail-editor");
        if (sender is UIElement element)
        {
            DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(element);
        }
    }

    internal void SetHostWindowActivated(bool isActivated)
    {
        _isHostWindowActivated = isActivated;
        ApplyHostFocusSelectionVisuals();
        if (!isActivated && DispatcherQueue is not null)
        {
            DispatcherQueue.TryEnqueue(ApplyHostFocusSelectionVisuals);
        }
    }

    private void TodoWidgetContent_GotFocus(object sender, RoutedEventArgs e)
    {
        ApplyHostFocusSelectionVisuals();
    }

    private void TodoWidgetContent_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DispatcherQueue is not null)
        {
            DispatcherQueue.TryEnqueue(ApplyHostFocusSelectionVisuals);
        }
        else
        {
            ApplyHostFocusSelectionVisuals();
        }
    }

    private bool HasHostKeyboardFocus()
    {
        if (_isDisposed || !IsLoaded)
        {
            return false;
        }

        return DeskBox.Views.WidgetWindowBase.IsOwnerWindowKeyboardFocused(this) ||
               (_isHostWindowActivated && XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is not null);
    }

    private void ApplyHostFocusSelectionVisuals()
    {
        if (_isDisposed || TodoListView is null)
        {
            return;
        }

        bool hasFocus = HasHostKeyboardFocus();
        foreach (object visibleItem in TodoListView.Items)
        {
            if (TodoListView.ContainerFromItem(visibleItem) is DependencyObject container)
            {
                ApplyHostFocusSelectionVisualToRow(container, hasFocus);
            }
        }
    }

    private void ApplyHostFocusSelectionVisualToRow(DependencyObject container, bool? hasFocusOverride = null)
    {
        bool hasFocus = hasFocusOverride ?? HasHostKeyboardFocus();
        if (FindVisualChild<Border>(container, "TodoDetailSelectionIndicator") is { } indicator)
        {
            indicator.Opacity = hasFocus ? 1.0 : 0.32;
        }

        if (FindVisualChild<Border>(container, "TodoDetailSelectionBackground") is { } detailBg)
        {
            detailBg.Opacity = hasFocus ? 0.11 : 0.04;
        }

        if (FindVisualChild<Border>(container, "TodoCopySelectionBackground") is { } copyBg)
        {
            copyBg.Opacity = hasFocus ? 0.14 : 0.05;
        }
    }

    public void OpenSearchBar()
    {
        if (_isDisposed || ViewModel is null)
        {
            return;
        }

        ViewModel.IsSearchOpen = true;
        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "todo-search");
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isDisposed || TodoSearchTextBox is null)
            {
                return;
            }

            DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(TodoSearchTextBox);
            TodoSearchTextBox.Focus(FocusState.Programmatic);
            TodoSearchTextBox.SelectAll();
        });
    }

    private void CloseSearchBar(bool focusList)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.SearchQuery = string.Empty;
        ViewModel.IsSearchOpen = false;
        if (focusList && !_isDisposed)
        {
            TodoListView?.Focus(FocusState.Programmatic);
        }
    }

    private void TodoSearchTextBox_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "todo-search");
        DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(TodoSearchTextBox);
    }

    private void TodoSearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseSearchBar(focusList: true);
            return;
        }

        if (e.Key == VirtualKey.Down && ViewModel?.VisibleItems.Count > 0)
        {
            e.Handled = true;
            SelectSingleTodoItemForKeyboard(ViewModel.VisibleItems[0]);
            TodoListView.Focus(FocusState.Programmatic);
        }
    }

    private void TodoSearchCloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseSearchBar(focusList: DeskBox.Views.WidgetWindowBase.IsOwnerWindowKeyboardFocused(this));
    }

}
