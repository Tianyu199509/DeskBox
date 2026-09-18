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

public sealed partial class TodoWidgetContent : UserControl
{
    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;
    private sealed record TodoSelectionHitTestItem(
        TodoItemViewModel Item,
        Windows.Foundation.Rect Bounds);

    private const int UndoToastMs = 4200;
    private const int CopyToastMs = 900;
    private const int CopyTapDelayMs = 210;

    private string? _draggedTodoItemId;
    private readonly List<string> _draggedTodoItemIds = [];
    private Border? _todoReorderDropTarget;
    private TodoItemViewModel? _editingItem;
    private TodoItemViewModel? _customDueDateItem;
    private IReadOnlyList<string>? _customDueDateItemIds;
    private TimeSpan _customDueTime = new(23, 59, 0);
    private string? _copySelectionAnchorId;
    private long _undoToastGeneration;
    private long _copyTapGeneration;
    private long _detailTransitionGeneration;
    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private bool _isClosingDetail;
    private bool _isSavingDetailDraft;
    private bool _isChangingTodoFilter;
    private Button? _pressedColorFilterButton;
    private bool _isStartingColorFilterDrag;
    private bool _colorFilterHandledEventsRegistered;
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

    public TodoWidgetContent()
    {
        InitializeComponent();
        InitializeDetailNotesAndSteps();
        DetailTitleTextBox.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(DetailTitleTextBox_KeyDown),
            handledEventsToo: true);
        Loaded += TodoWidgetContent_Loaded;
        Unloaded += TodoWidgetContent_Unloaded;
        ActualThemeChanged += (_, _) =>
        {
            ApplyEditorVisualStyle();
            ApplySelectionRectangleStyle();
        };
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
                _notesEditingItemId = null;
                oldViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                oldViewModel.VisibleItems.CollectionChanged -= VisibleItems_CollectionChanged;
            }

            DataContext = value;

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
    }

    private void TodoWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
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
        _ = SaveActiveNotesAsync(keepEditing: false);
        DetailPageTransitionHelper.Reset(DetailPage);
        CloseTodoEdit();
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
        CancelTodoSegmentedRestore();
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
            ShowUndoToast(ViewModel.UndoText, ViewModel.UndoActionText);
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

        if (e.PropertyName == nameof(TodoWidgetViewModel.IsCreatingDetailItem))
        {
            ApplyDetailSaveButtonVisibility();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.SelectedDetailItem))
        {
            ApplyDetailSaveButtonVisibility();
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
        if (TodoInlineEditor is null)
        {
            return;
        }

        var localization = App.Current.LocalizationService;
        DetailNotesEditor.TextResolver = localization.T;
        TodoInlineEditor.Title = localization.T("Todo.Menu.Edit");
        TodoInlineEditor.CancelText = localization.T("Common.Cancel");
        TodoInlineEditor.SaveText = localization.T("Common.Save");
        CustomDueDateTitleText.Text = localization.T("Todo.Due.Custom");
        CustomDueDatePicker.PlaceholderText = localization.T("Todo.Due.Custom");
        CustomDueDateCancelButton.Content = localization.T("Common.Cancel");
        CustomDueDateSaveButton.Content = localization.T("Common.Ok");
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
        MarkDetailSelectionExplicit();
        ViewModel.OpenNewDetail();
        DispatcherQueue.TryEnqueue(() =>
        {
            DetailTitleTextBox.Focus(FocusState.Programmatic);
            DetailTitleTextBox.SelectAll();
        });
    }

    private void AddCard_Click(object sender, RoutedEventArgs e)
    {
        OpenAddEditor();
    }

    private void ExpandInputButton_Click(object sender, RoutedEventArgs e)
    {
        OpenAddEditor();
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
            if (ViewModel.IsCreatingDetailItem)
            {
                if (!await SaveActiveNotesAsync(keepEditing: false))
                {
                    RefreshFilterButtons();
                    return;
                }

                bool hasTitle = !string.IsNullOrWhiteSpace(DetailTitleTextBox.Text);
                TodoItemViewModel? finalized = await ViewModel.FinalizeDetailAsync(
                    DetailTitleTextBox.Text,
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

            SelectFilter(filter);
        }
        finally
        {
            _isChangingTodoFilter = false;
        }

        EnsureWideDetailSelection();
    }

    private void DraftImportantButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.DraftImportant = !ViewModel.DraftImportant;
    }

    private void DraftDueDateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel is null)
        {
            return;
        }

        var flyout = CreateDraftDueDateFlyout();
        flyout.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    private MenuFlyout CreateDraftDueDateFlyout()
    {
        var flyout = new MenuFlyout();
        var localization = App.Current.LocalizationService;

        var todayItem = new MenuFlyoutItem { Text = localization.T("Todo.Due.Today") };
        todayItem.Click += (_, _) => ViewModel?.SetDraftDueDatePreset(TodoDuePreset.Today);
        flyout.Items.Add(todayItem);

        var tomorrowItem = new MenuFlyoutItem { Text = localization.T("Todo.Due.Tomorrow") };
        tomorrowItem.Click += (_, _) => ViewModel?.SetDraftDueDatePreset(TodoDuePreset.Tomorrow);
        flyout.Items.Add(tomorrowItem);

        var thisWeekItem = new MenuFlyoutItem { Text = localization.T("Todo.Due.ThisWeek") };
        thisWeekItem.Click += (_, _) => ViewModel?.SetDraftDueDatePreset(TodoDuePreset.ThisWeek);
        flyout.Items.Add(thisWeekItem);

        var nextMondayItem = new MenuFlyoutItem { Text = localization.T("Todo.Due.NextMonday") };
        nextMondayItem.Click += (_, _) => ViewModel?.SetDraftDueDatePreset(TodoDuePreset.NextMonday);
        flyout.Items.Add(nextMondayItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var customItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Due.Custom"),
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        customItem.Click += async (_, _) => await PickCustomDueDateAsync(null);
        flyout.Items.Add(customItem);

        if (ViewModel?.DraftDueDate is not null)
        {
            var clearItem = new MenuFlyoutItem
            {
                Text = localization.T("Todo.Due.Clear"),
                Icon = new FontIcon { Glyph = "\uE711" }
            };
            clearItem.Click += (_, _) => ViewModel?.SetDraftDueDatePreset(TodoDuePreset.Clear);
            flyout.Items.Add(clearItem);
        }

        return flyout;
    }

    private void MetadataImportant_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item ||
            ViewModel is null)
        {
            return;
        }

        _ = SetImportantWithFeedbackAsync(item, !item.IsImportant);
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

    private async void InlineEditTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item ||
            ViewModel is null)
        {
            return;
        }

        if (!item.IsEditing)
        {
            return;
        }

        _ = await ViewModel.CommitEditAsync(item.Id);
    }

    private async void InlineEditTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item ||
            ViewModel is null)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.CancelEdit(item.Id);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (ShouldSubmitTodoEditor(e))
            {
                _ = await ViewModel.CommitEditAsync(item.Id);
            }
            else if (sender is TextBox textBox)
            {
                TextBoxEditorShortcutHelper.InsertLineBreak(textBox);
            }
        }
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
            if (!await SaveActiveNotesAsync(keepEditing: false))
            {
                return;
            }

            TodoItemViewModel? finalizedItem = await ViewModel.FinalizeDetailAsync(
                DetailTitleTextBox.Text,
                closeDetail: false);
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
            Focus(FocusState.Programmatic);
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
        long generation = ++_detailTransitionGeneration;
        DetailPage.IsHitTestVisible = false;
        await DetailPageTransitionHelper.PlayExitAsync(DetailPage);
        return generation == _detailTransitionGeneration &&
               ReferenceEquals(ViewModel?.SelectedDetailItem, expectedItem);
    }

    private void ResetDetailTransition()
    {
        DetailPageTransitionHelper.Reset(DetailPage);
        DetailPage.IsHitTestVisible = true;
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

        if (ViewModel.IsCreatingDetailItem)
        {
            return true;
        }

        return await ViewModel.UpdateItemTextAsync(item.Id, DetailTitleTextBox.Text);
    }

    private async void DetailSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSavingDetailDraft ||
            ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DetailTitleTextBox.Text))
        {
            DetailTitleTextBox.Focus(FocusState.Programmatic);
            return;
        }

        _isSavingDetailDraft = true;
        try
        {
            if (!await SaveActiveNotesAsync(keepEditing: false))
            {
                return;
            }

            TodoItemViewModel? savedItem;
            if (ViewModel.IsCreatingDetailItem)
            {
                savedItem = await ViewModel.FinalizeDetailAsync(
                    DetailTitleTextBox.Text,
                    closeDetail: false);
                if (savedItem is null)
                {
                    DetailTitleTextBox.Focus(FocusState.Programmatic);
                    return;
                }
            }
            else
            {
                if (!await ViewModel.UpdateItemTextAsync(item.Id, DetailTitleTextBox.Text))
                {
                    DetailTitleTextBox.Focus(FocusState.Programmatic);
                    return;
                }

                savedItem = item;
            }

            ApplyMasterDetailVisibility();
            TodoListView.ScrollIntoView(savedItem);
            ShowTodoStatus("Todo.Status.Saved");
        }
        finally
        {
            _isSavingDetailDraft = false;
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
        if (ViewModel is { IsCreatingDetailItem: false, SelectedDetailItem: { } item })
        {
            await ViewModel.UpdateItemTextAsync(item.Id, DetailTitleTextBox.Text);
        }
    }

    private async void DetailTitleTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
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

}
