using System.ComponentModel;
using System.Numerics;
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
    private sealed record TodoSelectionHitTestItem(
        TodoItemViewModel Item,
        Windows.Foundation.Rect Bounds);

    private const int UndoToastMs = 4200;
    private const int CopyToastMs = 900;
    private const int CopyTapDelayMs = 210;

    private string? _draggedTodoItemId;
    private readonly List<string> _draggedTodoItemIds = [];
    private bool _todoTabDropHandled;
    private TodoItemViewModel? _editingItem;
    private TodoItemViewModel? _customDueDateItem;
    private IReadOnlyList<string>? _customDueDateItemIds;
    private MenuFlyout? _pendingConfirmFlyout;
    private TimeSpan _customDueTime = new(23, 59, 0);
    private string? _copySelectionAnchorId;
    private long _undoToastGeneration;
    private long _copyTapGeneration;
    private long _detailTransitionGeneration;
    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private bool _isClosingDetail;
    private bool _isResizingDetailTitle;
    private Button? _pressedColorFilterButton;
    private bool _isStartingColorFilterDrag;
    private bool _colorFilterHandledEventsRegistered;
    private DateTimeOffset _suppressColorFilterClickUntil;
    private double _detailTitleResizeStartY;
    private double _detailTitleResizeStartHeight;
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

    public void RevealReminderItem(string? itemId, bool preferTodayFilter)
    {
        if (ViewModel is null)
        {
            return;
        }

        var item = ViewModel.FocusReminderItem(itemId, preferTodayFilter);
        RefreshFilterButtons();
        TodoListView.Focus(FocusState.Programmatic);

        if (item is not null)
        {
            _copySelectionAnchorId = item.Id;
            TodoListView.ScrollIntoView(item);
        }
    }

    public TodoWidgetViewModel? ViewModel
    {
        get => DataContext as TodoWidgetViewModel;
        set
        {
            if (DataContext is TodoWidgetViewModel oldViewModel)
            {
                oldViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            DataContext = value;

            if (value is not null)
            {
                value.PropertyChanged += ViewModel_PropertyChanged;
            }

            RefreshFilterButtons();
        }
    }

    private void TodoWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
        App.Current.LocalizationService.LanguageChanged += OnLanguageChanged;
        ApplyLocalizedText();
        ApplyEditorVisualStyle();
        ApplySelectionRectangleStyle();
        RegisterColorFilterHandledEvents();
        RefreshFilterButtons();
    }

    private void TodoWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
        _copyTapGeneration++;
        _detailTransitionGeneration++;
        DetailPageTransitionHelper.Reset(DetailPage);
        CloseTodoEdit();
        CloseCustomDueDateOverlay();
    }

    private void TodoFilterSegmented_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySegmentedStyle();
    }

    private void TodoFilterSegmented_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplySegmentedLayout();
    }

    private void ApplySegmentedLayout()
    {
        if (ViewModel?.TabStyle == SettingsService.WidgetTabStyleButton)
        {
            WidgetSegmentedLayoutHelper.ApplyEqualItemWidths(TodoFilterSegmented);
        }
        else
        {
            WidgetSegmentedLayoutHelper.ApplyNaturalItemWidths(TodoFilterSegmented);
        }
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

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoWidgetViewModel.SelectedFilter))
        {
            ClearCopySelection();
            ViewModel?.CollapseAllExpanded();
            RefreshFilterButtons();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.TabStyle))
        {
            ApplySegmentedStyle();
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
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.UndoText) &&
            ViewModel is { CanUndoLastAction: true })
        {
            ShowUndoToast(ViewModel.UndoText, ViewModel.UndoActionText);
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.IsDetailPageOpen) &&
            ViewModel?.IsDetailPageOpen == true)
        {
            QueueDetailEnterAnimation();
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
        TodoInlineEditor.Title = localization.T("Todo.Menu.Edit");
        TodoInlineEditor.CancelText = localization.T("Common.Cancel");
        TodoInlineEditor.SaveText = localization.T("Common.Save");
        CustomDueDateTitleText.Text = localization.T("Todo.Due.Custom");
        CustomDueDatePicker.PlaceholderText = localization.T("Todo.Due.Custom");
        CustomDueDateCancelButton.Content = localization.T("Common.Cancel");
        CustomDueDateSaveButton.Content = localization.T("Common.Ok");
    }

    public void OpenAddEditor()
    {
        if (ViewModel is null)
        {
            return;
        }

        ClearCopySelection();
        CloseCustomDueDateOverlay();
        CloseTodoEdit();
        ViewModel.OpenNewDetail();
        DetailTitleTextBox.Height = 64;
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

    private void TodoFilterSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectFilter(GetSelectedSegmentFilter());
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

        _ = ViewModel.SetImportantAsync(item.Id, !item.IsImportant);
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
            TodoItemViewModel? finalizedItem = await ViewModel.FinalizeDetailAsync(
                DetailTitleTextBox.Text,
                closeDetail: false);
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

    private async Task SaveDetailEditorsAsync(TodoItemViewModel item)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.UpdateItemTextAsync(item.Id, DetailTitleTextBox.Text);
    }

    private async void DetailCompletionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedDetailItem is not { } item || sender is not FrameworkElement element)
        {
            return;
        }

        PlayCompletionToggleAnimation(element);
        await ViewModel.SetCompletedAsync(item.Id, !item.IsCompleted);
        ApplyDetailCompletionVisualState();
    }

    private async void DetailImportantButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        await ViewModel.SetImportantAsync(item.Id, !item.IsImportant);
    }

    private async void DetailTitleTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedDetailItem is { } item)
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

    private void DetailTitleResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement handle)
        {
            return;
        }

        _isResizingDetailTitle = true;
        _detailTitleResizeStartY = e.GetCurrentPoint(DetailPage).Position.Y;
        _detailTitleResizeStartHeight = DetailTitleTextBox.ActualHeight;
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void DetailTitleResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingDetailTitle)
        {
            return;
        }

        double currentY = e.GetCurrentPoint(DetailPage).Position.Y;
        double maxHeight = Math.Max(64, Math.Min(180, DetailPage.ActualHeight * 0.45));
        DetailTitleTextBox.Height = Math.Clamp(
            _detailTitleResizeStartHeight + currentY - _detailTitleResizeStartY,
            36,
            maxHeight);
        e.Handled = true;
    }

    private void DetailTitleResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isResizingDetailTitle = false;
        if (sender is FrameworkElement handle)
        {
            handle.ReleasePointerCapture(e.Pointer);
        }

        e.Handled = true;
    }

    private void DetailTitleResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isResizingDetailTitle = false;
    }

    private async void DetailDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        await SaveDetailEditorsAsync(item);
        await DeleteItemAsync(item, anchor);
    }
}
