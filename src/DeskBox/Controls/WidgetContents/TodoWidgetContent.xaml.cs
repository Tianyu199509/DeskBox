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
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

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
    private TodoItemViewModel? _editingItem;
    private TodoItemViewModel? _customDueDateItem;
    private IReadOnlyList<string>? _customDueDateItemIds;
    private MenuFlyout? _pendingConfirmFlyout;
    private TimeSpan _customDueTime = new(23, 59, 0);
    private string? _copySelectionAnchorId;
    private long _undoToastGeneration;
    private long _copyTapGeneration;
    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
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

    private void RedColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Red);
    }

    private void OrangeColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Orange);
    }

    private void YellowColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Yellow);
    }

    private void GreenColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Green);
    }

    private void BlueColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Blue);
    }

    private void PurpleColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Purple);
    }

    private void TealColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Teal);
    }

    private void PinkColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Pink);
    }

    private void ColorFilterButton_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string colorMarker } ||
            TodoItem.NormalizeColorMarker(colorMarker) is null)
        {
            e.Cancel = true;
            return;
        }

        DeskBoxDragData.SetTodoColorMarker(e.Data, colorMarker);
        e.Data.RequestedOperation = DataPackageOperation.Link;
        e.Data.Properties.Title = App.Current.LocalizationService.T(
            TodoItem.GetColorMarkerLocalizationKey(colorMarker));
    }

    private void ColorFilterButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button &&
            e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            _pressedColorFilterButton = button;
            _colorFilterDragStartPoint = e.GetCurrentPoint(RootGrid).Position;
        }
    }

    private void RegisterColorFilterHandledEvents()
    {
        if (_colorFilterHandledEventsRegistered)
        {
            return;
        }

        _colorFilterHandledEventsRegistered = true;
        foreach (Button button in new[]
                 {
                     RedColorFilterButton,
                     OrangeColorFilterButton,
                     YellowColorFilterButton,
                     GreenColorFilterButton,
                     BlueColorFilterButton,
                     PurpleColorFilterButton,
                     TealColorFilterButton,
                     PinkColorFilterButton
                 })
        {
            button.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(ColorFilterButton_PointerPressed),
                handledEventsToo: true);
            button.AddHandler(
                UIElement.PointerMovedEvent,
                new PointerEventHandler(ColorFilterButton_PointerMoved),
                handledEventsToo: true);
        }
    }

    private async void ColorFilterButton_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isStartingColorFilterDrag ||
            sender is not Button button ||
            !ReferenceEquals(button, _pressedColorFilterButton))
        {
            return;
        }

        var point = e.GetCurrentPoint(RootGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pressedColorFilterButton = null;
            return;
        }

        double deltaX = point.Position.X - _colorFilterDragStartPoint.X;
        double deltaY = point.Position.Y - _colorFilterDragStartPoint.Y;
        if ((deltaX * deltaX) + (deltaY * deltaY) < 25)
        {
            return;
        }

        _isStartingColorFilterDrag = true;
        _suppressColorFilterClickUntil = DateTimeOffset.UtcNow.AddMilliseconds(500);
        e.Handled = true;
        try
        {
            await button.StartDragAsync(e.GetCurrentPoint(button));
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to start color marker drag: {ex.Message}");
        }
        finally
        {
            _suppressColorFilterClickUntil = DateTimeOffset.UtcNow.AddMilliseconds(350);
            _pressedColorFilterButton = null;
            _isStartingColorFilterDrag = false;
        }
    }

    private void ColorFilterButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressedColorFilterButton = null;
    }

    private void ColorFilterButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isStartingColorFilterDrag)
        {
            _pressedColorFilterButton = null;
        }
    }

    private void TodoItem_DragOver(object sender, DragEventArgs e)
    {
        ResetTodoReorderVisualState();

        if (e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat))
        {
            e.Handled = true;
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.IsGlyphVisible = true;
            SetTodoItemHoverState(sender as DependencyObject, true);
            return;
        }

        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.Handled = true;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.IsGlyphVisible = true;
            SetTodoItemHoverState(sender as DependencyObject, true);
        }
    }

    private void TodoItem_DragLeave(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat) ||
            DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.Handled = true;
            SetTodoItemHoverState(sender as DependencyObject, false);
        }

        ResetTodoReorderVisualState();
    }

    private async void TodoItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoItemViewModel item } || ViewModel is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ResetTodoReorderVisualState();
            return;
        }

        if (e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat))
        {
            e.Handled = true;
            SetTodoItemHoverState(sender as DependencyObject, false);
            string? colorMarker = TodoItem.NormalizeColorMarker(
                await DeskBoxDragData.TryGetTodoColorMarkerAsync(e.DataView));
            if (colorMarker is null)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                ResetTodoReorderVisualState();
                return;
            }

            await ViewModel.SetColorMarkerAsync(item.Id, colorMarker);
            e.AcceptedOperation = DataPackageOperation.Link;
            ResetTodoReorderVisualState();
            return;
        }

        if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            ResetTodoReorderVisualState();
            return;
        }

        e.Handled = true;
        SetTodoItemHoverState(sender as DependencyObject, false);
        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            int addedCount = await ViewModel.AddDroppedAttachmentsAsync(item.Id, batch.Files);
            e.AcceptedOperation = addedCount > 0
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to attach dropped files: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
            ResetTodoReorderVisualState();
        }
    }

    private void SelectColorFilter(TodoColorFilter filter)
    {
        if (DateTimeOffset.UtcNow <= _suppressColorFilterClickUntil)
        {
            return;
        }

        if (ViewModel is null)
        {
            return;
        }

        ViewModel.SetColorFilter(ViewModel.SelectedColorFilter == filter
            ? TodoColorFilter.All
            : filter);
        RefreshFilterButtons();
    }

    private void SelectFilter(TodoFilter filter)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.SelectedFilter == filter)
        {
            RefreshFilterButtons();
            return;
        }

        ViewModel.SetFilter(filter);
        RefreshFilterButtons();
    }

    private void RefreshFilterButtons()
    {
        if (TodoFilterSegmented is null || ViewModel is null)
        {
            return;
        }

        int selectedIndex = GetFilterSegmentIndex(ViewModel.SelectedFilter);
        if (TodoFilterSegmented.SelectedIndex != selectedIndex)
        {
            TodoFilterSegmented.SelectedIndex = selectedIndex;
        }

        ApplyColorFilterButtonState(RedColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Red);
        ApplyColorFilterButtonState(OrangeColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Orange);
        ApplyColorFilterButtonState(YellowColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Yellow);
        ApplyColorFilterButtonState(GreenColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Green);
        ApplyColorFilterButtonState(BlueColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Blue);
        ApplyColorFilterButtonState(PurpleColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Purple);
        ApplyColorFilterButtonState(TealColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Teal);
        ApplyColorFilterButtonState(PinkColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Pink);
    }

    private TodoFilter GetSelectedSegmentFilter()
    {
        return TodoFilterSegmented?.SelectedIndex switch
        {
            1 => TodoFilter.Today,
            2 => TodoFilter.Important,
            3 => TodoFilter.Completed,
            _ => TodoFilter.All
        };
    }

    private static int GetFilterSegmentIndex(TodoFilter filter)
    {
        return filter switch
        {
            TodoFilter.Today => 1,
            TodoFilter.Important => 2,
            TodoFilter.Completed => 3,
            _ => 0
        };
    }

    private void ApplyColorFilterButtonState(Button button, bool isSelected)
    {
        button.Style = (Style)Resources[isSelected
            ? "TodoColorFilterSelectedButtonStyle"
            : "TodoColorFilterButtonStyle"];
    }

    private async void ItemCompletionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        PlayCompletionToggleAnimation(element);
        await ViewModel.SetCompletedAsync(item.Id, !item.IsCompleted);
        ApplyDetailCompletionVisualState();
    }

    private static void PlayCompletionToggleAnimation(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (visual is null)
        {
            return;
        }

        var compositor = visual.Compositor;
        if (compositor is null)
        {
            return;
        }

        visual.StopAnimation("Scale");
        visual.CenterPoint = new Vector3(
            (float)(element.ActualSize.X * 0.5),
            (float)(element.ActualSize.Y * 0.5),
            0f);

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1.0f),
            new Vector2(0.3f, 1.0f));

        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(300);
        scaleAnimation.InsertKeyFrame(0.0f, new Vector3(1.0f, 1.0f, 1.0f));
        scaleAnimation.InsertKeyFrame(0.4f, new Vector3(1.3f, 1.3f, 1.0f), easing);
        scaleAnimation.InsertKeyFrame(1.0f, new Vector3(1.0f, 1.0f, 1.0f), easing);

        visual.StartAnimation("Scale", scaleAnimation);
    }

    private async void ImportantItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await ViewModel.SetImportantAsync(item.Id, !item.IsImportant);
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await DeleteItemAsync(item, element);
    }

    private void RecurringHistoryToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        ViewModel.ToggleRecurringHistoryGroup(item.RecurrenceSeriesId);
    }

    private void TodoListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var draggedItem = e.Items.OfType<TodoItemViewModel>().FirstOrDefault();
        var selectedItems = GetSelectedCopyItemsInVisibleOrder();
        if (draggedItem is not null &&
            selectedItems.Count > 0 &&
            selectedItems.Contains(draggedItem))
        {
            _draggedTodoItemId = null;
            TodoListView.CanReorderItems = false;
            string text = selectedItems.Count == 1
                ? TodoClipboardFormatter.FormatSingle(selectedItems[0], App.Current.LocalizationService)
                : TodoClipboardFormatter.FormatBatch(selectedItems, App.Current.LocalizationService);
            if (string.IsNullOrWhiteSpace(text))
            {
                e.Cancel = true;
                ResetTodoReorderVisualState();
                return;
            }

            DeskBoxDragData.SetText(e.Data, text, DeskBoxDragData.SourceTodo);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            e.Data.Properties.Title = App.Current.LocalizationService.Format("Todo.CopiedCount", selectedItems.Count);
            return;
        }

        if (selectedItems.Count > 0)
        {
            ClearCopySelection();
        }

        _draggedTodoItemId = draggedItem?.Id;
        if (draggedItem is not null)
        {
            TodoListView.CanReorderItems = true;
            DeskBoxDragData.SetText(
                e.Data,
                TodoClipboardFormatter.FormatSingle(draggedItem, App.Current.LocalizationService),
                DeskBoxDragData.SourceTodo);
            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        }
        else
        {
            e.Cancel = true;
            ResetTodoReorderVisualState();
        }
    }

    private void TodoItem_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        item.PropertyChanged -= TodoItem_PropertyChanged;
        item.PropertyChanged += TodoItem_PropertyChanged;
        ApplyTodoItemTooltips(sender, item);
        SetTodoItemHoverState(sender, false);
    }

    private async void TodoListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            _draggedTodoItemId = null;
            ResetTodoReorderVisualState();
            return;
        }

        try
        {
            var movedItem = ViewModel.VisibleItems.FirstOrDefault(item =>
                string.Equals(item.Id, _draggedTodoItemId, StringComparison.Ordinal));
            if (movedItem is not null)
            {
                await ViewModel.MoveItemAsync(movedItem.Id, ViewModel.VisibleItems.IndexOf(movedItem));
            }
        }
        finally
        {
            _draggedTodoItemId = null;
            ResetTodoReorderVisualState();
        }
    }

    private async void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            return;
        }

        ResetTodoReorderVisualState();
        await HandleExternalTextDragOverAsync(e);
    }

    private async void TodoListView_DragOver(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            return;
        }

        ResetTodoReorderVisualState();
        await HandleExternalTextDragOverAsync(e);
    }

    private void ExternalTodoDrag_DragLeave(object sender, DragEventArgs e)
    {
        ResetTodoReorderVisualState();
    }

    private async Task HandleExternalTextDragOverAsync(DragEventArgs e)
    {
        if (ViewModel is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            e.AcceptedOperation = DeskBoxDragData.HasDroppedFiles(e.DataView) ||
                                  await HasDroppedTodoTextAsync(e.DataView)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = e.AcceptedOperation != DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
            ResetTodoReorderVisualState();
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            if (DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
                if (batch.Files.Count == 0 || ViewModel is null)
                {
                    string? fallbackText = await TryGetDroppedTodoTextAsync(e.DataView);
                    if (string.IsNullOrWhiteSpace(fallbackText) || ViewModel is null)
                    {
                        e.AcceptedOperation = DataPackageOperation.None;
                        return;
                    }

                    await ViewModel.AddItemAsync(fallbackText);
                    e.AcceptedOperation = DataPackageOperation.Copy;
                    return;
                }

                TodoItemViewModel? targetItem = ViewModel.IsDetailPageOpen
                    ? ViewModel.SelectedDetailItem
                    : await ViewModel.AddItemAsync(BuildDroppedTodoTitle(batch.Files));
                if (targetItem is null)
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    return;
                }

                int addedCount = await ViewModel.AddDroppedAttachmentsAsync(targetItem.Id, batch.Files);
                e.AcceptedOperation = addedCount > 0
                    ? DataPackageOperation.Copy
                    : DataPackageOperation.None;
                if (addedCount > 0)
                {
                    ShowUndoToast(
                        App.Current.LocalizationService.T("Todo.Dropped"),
                        durationMs: CopyToastMs,
                        clearUndoOnHide: false);
                }
                return;
            }

            string? text = await TryGetDroppedTodoTextAsync(e.DataView);
            if (string.IsNullOrWhiteSpace(text) || ViewModel is null)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            await ViewModel.AddItemAsync(text);
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.Dropped"),
                durationMs: CopyToastMs,
                clearUndoOnHide: false);
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to import dropped text: {ex}");
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.DropFailed"),
                durationMs: UndoToastMs,
                clearUndoOnHide: false);
        }
        finally
        {
            deferral.Complete();
            ResetTodoReorderVisualState();
        }
    }

    private static async Task<bool> HasDroppedTodoTextAsync(DataPackageView dataView)
    {
        string? text = await TryGetDroppedTodoTextAsync(dataView);
        return !string.IsNullOrWhiteSpace(text);
    }

    private static async Task<string?> TryGetDroppedTodoTextAsync(DataPackageView dataView)
    {
        if (dataView.Contains(StandardDataFormats.StorageItems))
        {
            return null;
        }

        string? text = await DeskBoxDragData.TryGetTextAsync(dataView);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        return text.Length <= QuickCaptureClipboardService.MaxClipboardTextCharacters
            ? text
            : text[..QuickCaptureClipboardService.MaxClipboardTextCharacters].Trim();
    }

    private static string BuildDroppedTodoTitle(IReadOnlyList<DroppedFilePath> files)
    {
        string title = files.Count == 1
            ? System.IO.Path.GetFileNameWithoutExtension(files[0].DisplayName)
            : string.Join(", ", files.Take(3).Select(file => file.DisplayName));
        if (files.Count > 3)
        {
            title = $"{title} +{files.Count - 3}";
        }

        return string.IsNullOrWhiteSpace(title) ? "Attachment" : title;
    }

    private void TodoListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool isCtrlPressed = Win32Helper.IsKeyPressed(VirtualKey.Control);
        if (isCtrlPressed && e.Key == VirtualKey.A)
        {
            SelectAllVisibleTodoItems();
            e.Handled = true;
            return;
        }

        if (isCtrlPressed && e.Key == VirtualKey.C)
        {
            CopySelectedTodoItems();
            e.Handled = true;
            return;
        }

                if (e.Key == VirtualKey.Escape && HasCopySelection())
        {
            ClearCopySelection();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape &&
            ViewModel?.Items.Any(item => item.IsExpanded) == true)
        {
            ViewModel.CollapseAllExpanded();
            e.Handled = true;
        }
    }

    private async void TodoWidgetContent_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || ViewModel is null)
        {
            return;
        }

        if (CustomDueDateOverlay.Visibility == Visibility.Visible)
        {
            CloseCustomDueDateOverlay();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsDetailPageOpen)
        {
            await CloseDetailAsync();
            e.Handled = true;
        }
    }

    private void TodoListView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        var properties = e.GetCurrentPoint(listView).Properties;
        bool canStartReorder = properties.IsLeftButtonPressed &&
                               e.OriginalSource is DependencyObject source &&
                               FindTodoItemContainer(source) is not null &&
                               !IsInteractiveTodoSource(source) &&
                               !Win32Helper.IsKeyPressed(VirtualKey.Shift) &&
                               !Win32Helper.IsKeyPressed(VirtualKey.Control);
        listView.CanReorderItems = canStartReorder;

        if (!properties.IsLeftButtonPressed ||
            Win32Helper.IsKeyPressed(VirtualKey.Shift) ||
            !CanStartTodoBoxSelection(e.OriginalSource))
        {
            if (CanStartTodoBoxSelection(e.OriginalSource))
            {
                ViewModel?.CollapseAllExpanded();
            }
            return;
        }

        _copyTapGeneration++;
        TodoListView.Focus(FocusState.Programmatic);
        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        _selectionStartPoint = e.GetCurrentPoint(TodoSelectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot = Win32Helper.IsKeyPressed(VirtualKey.Control)
            ? GetSelectedCopyItemsInVisibleOrder().ToList()
            : [];
        _selectionPreviewItems = new HashSet<TodoItemViewModel>(_selectionSnapshot);
        _selectionHitTestItems = [];

        if (!Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            ClearCopySelection();
        }
    }

    private void TodoListView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView || !_selectionPointerPressed)
        {
            return;
        }

        _selectionCurrentPoint = e.GetCurrentPoint(TodoSelectionOverlay).Position;
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
                CacheTodoSelectionHitTestItems(listView);
            }
        }

        UpdateTodoSelectionRectangleVisual();
        ApplyTodoSelectionRectanglePreview();
        e.Handled = true;
    }

    private void TodoListView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        bool wasBoxSelecting = _isBoxSelecting;
        FinishTodoSelectionRectangle(listView);
        if (wasBoxSelecting)
        {
            listView.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        ResetTodoReorderVisualState();
    }

    private void TodoListView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            FinishTodoSelectionRectangle(listView);
            ResetTodoReorderVisualState();
        }
    }

    private void ResetTodoReorderVisualState()
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            return;
        }

        bool wasReorderEnabled = TodoListView.CanReorderItems;
        TodoListView.CanReorderItems = false;
        if (!wasReorderEnabled)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
            {
                return;
            }

            TodoListView.InvalidateMeasure();
            TodoListView.InvalidateArrange();
            TodoListView.UpdateLayout();
        });
    }

    private void TodoItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetTodoItemHoverState(sender as DependencyObject, true);
    }

    private void TodoItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetTodoItemHoverState(sender as DependencyObject, false);
    }

    private void TodoItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TodoItemViewModel item)
        {
            return;
        }

        if (e.PropertyName is not (nameof(TodoItemViewModel.IsCompleted) or nameof(TodoItemViewModel.ColorMarker)))
        {
            return;
        }

        foreach (var visibleItem in TodoListView.Items)
        {
            if (ReferenceEquals(visibleItem, item) &&
                TodoListView.ContainerFromItem(visibleItem) is FrameworkElement container)
            {
                ApplyTodoItemTooltips(container, item);
                SetTodoItemHoverState(container, false);
                break;
            }
        }
    }

    private static void ApplyTodoItemTooltips(DependencyObject itemRoot, TodoItemViewModel item)
    {
        var localization = App.Current.LocalizationService;

        _ = localization;
    }

        private void TodoItemContent_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Single tap handles expand/edit; double tap just prevents default
        e.Handled = true;
    }

        private async void TodoItemContent_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        TodoListView.Focus(FocusState.Programmatic);
        bool isCtrlPressed = Win32Helper.IsKeyPressed(VirtualKey.Control);
        bool isShiftPressed = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        if (isCtrlPressed || isShiftPressed)
        {
            _copyTapGeneration++;
            if (isShiftPressed)
            {
                SelectTodoRange(item);
            }
            else
            {
                ToggleTodoSelection(item);
            }

            e.Handled = true;
            return;
        }

        if (HasCopySelection())
        {
            ClearCopySelection();
            e.Handled = true;
            return;
        }

        if (ViewModel is null)
        {
            return;
        }

        if (!item.IsExpanded)
        {
            ViewModel.ToggleExpanded(item.Id);
            TodoListView.ScrollIntoView(item);
        }
        else if (!item.IsEditing)
        {
            ViewModel.BeginEdit(item.Id);
        }

        e.Handled = true;
    }

    private void TodoItemCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item ||
            ViewModel is null ||
            IsInteractiveTodoSource(e.OriginalSource))
        {
            return;
        }

        ClearCopySelection();
        ClearTodoListContainerSelection();
        if (ViewModel.OpenDetail(item.Id) is null)
        {
            return;
        }

        DetailTitleTextBox.Height = 64;
        ApplyDetailCompletionVisualState();
        DispatcherQueue.TryEnqueue(() =>
        {
            DetailTitleTextBox.Focus(FocusState.Programmatic);
            DetailTitleTextBox.Select(DetailTitleTextBox.Text?.Length ?? 0, 0);
        });
        e.Handled = true;
    }

    private void TodoListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TodoItemViewModel item || ViewModel is null)
        {
            return;
        }

        ClearCopySelection();
        ClearTodoListContainerSelection();
        if (ViewModel.OpenDetail(item.Id) is null)
        {
            return;
        }

        DetailTitleTextBox.Height = 64;
        ApplyDetailCompletionVisualState();
        DispatcherQueue.TryEnqueue(() =>
        {
            DetailTitleTextBox.Focus(FocusState.Programmatic);
            DetailTitleTextBox.Select(DetailTitleTextBox.Text?.Length ?? 0, 0);
        });
    }

    private static bool IsInteractiveTodoSource(object? source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null)
        {
            if (current is ButtonBase or TextBox or CheckBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ClearTodoListContainerSelection()
    {
        TodoListView.SelectedItem = null;
        foreach (object visibleItem in TodoListView.Items)
        {
            if (TodoListView.ContainerFromItem(visibleItem) is SelectorItem container)
            {
                container.IsSelected = false;
            }
        }
    }

    private void TodoItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        TodoListView.Focus(FocusState.Programmatic);
        if (!item.IsCopySelected)
        {
            ClearCopySelection();
            item.IsCopySelected = true;
            _copySelectionAnchorId = item.Id;
        }

        CreateItemFlyout(item, element).ShowAt(
            element,
            new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private MenuFlyout CreateItemFlyout(TodoItemViewModel item, FrameworkElement anchor)
    {
        var selectedItems = GetSelectedCopyItemsInVisibleOrder();
        if (selectedItems.Count > 1 && item.IsCopySelected)
        {
            return CreateMultiItemFlyout(selectedItems, anchor);
        }

        var flyout = new MenuFlyout();
        var localization = App.Current.LocalizationService;

        var editItem = CreateTodoContextCommand("Todo.Menu.Edit", "\uE70F");
        editItem.Click += (_, _) =>
        {
            flyout.Hide();
            ClearCopySelection();
            ClearTodoListContainerSelection();
            CloseCustomDueDateOverlay();
            if (ViewModel?.OpenDetail(item.Id) is null)
            {
                return;
            }

            DetailTitleTextBox.Height = 64;
            ApplyDetailCompletionVisualState();
            DispatcherQueue.TryEnqueue(() =>
            {
                DetailTitleTextBox.Focus(FocusState.Programmatic);
                DetailTitleTextBox.Select(DetailTitleTextBox.Text?.Length ?? 0, 0);
            });
        };
        flyout.Items.Add(editItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = selectedItems.Count > 1 && item.IsCopySelected
                ? localization.Format("Todo.Menu.CopySelected", selectedItems.Count)
                : localization.T("Todo.Menu.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += (_, _) =>
        {
            flyout.Hide();
            if (selectedItems.Count > 1 && item.IsCopySelected)
            {
                CopySelectedTodoItems(selectedItems);
            }
            else
            {
                CopyTodoItemText(item);
            }
        };
        flyout.Items.Add(copyItem);

        var deleteItem = CreateTodoContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += (_, _) =>
        {
            flyout.Hide();
            DispatcherQueue.TryEnqueue(() => ShowDeleteItemConfirmation(item, anchor));
        };
        flyout.Items.Add(new MenuFlyoutSeparator());

        var colorMenu = CreateLazyTodoMenu(flyout, menu =>
        {
            menu.Items.Add(CreateColorMarkerItem(item, null));
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (string colorMarker in TodoItem.SupportedColorMarkers)
            {
                menu.Items.Add(CreateColorMarkerItem(item, colorMarker));
            }
        });
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.ColorMarker", "\uE790", colorMenu, flyout));

        var dueDateMenu = CreateLazyTodoMenu(flyout, menu =>
        {
            menu.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Today, localization.T("Todo.Due.Today")));
            menu.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Tomorrow, localization.T("Todo.Due.Tomorrow")));
            menu.Items.Add(CreateDuePresetItem(item, TodoDuePreset.ThisWeek, localization.T("Todo.Due.ThisWeek")));
            menu.Items.Add(CreateDuePresetItem(item, TodoDuePreset.NextMonday, localization.T("Todo.Due.NextMonday")));
            menu.Items.Add(new MenuFlyoutSeparator());
            var customDueDateItem = new MenuFlyoutItem
            {
                Text = localization.T("Todo.Due.Custom"),
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            customDueDateItem.Click += async (_, _) => await PickCustomDueDateAsync(item);
            menu.Items.Add(customDueDateItem);
            if (item.DueDate is not null)
            {
                menu.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Clear, localization.T("Todo.Due.Clear")));
            }
        });
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.DueDate", "\uE787", dueDateMenu, flyout));

        var reminderMenu = CreateLazyTodoMenu(flyout, menu =>
        {
            menu.Items.Add(CreateReminderOffsetItem(item, null));
            menu.Items.Add(CreateReminderOffsetItem(item, TodoReminderOptions.ReminderOff));
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (int offsetMinutes in TodoReminderOptions.SupportedOffsetMinutes)
            {
                menu.Items.Add(CreateReminderOffsetItem(item, offsetMinutes));
            }
        });
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.Reminder", "\uEA8F", reminderMenu, flyout, item.DueDate is not null));

        var recurrenceMenu = CreateLazyTodoMenu(flyout, menu =>
        {
            foreach (string recurrenceMode in TodoRecurrenceMode.SupportedModes)
            {
                menu.Items.Add(CreateRecurrenceItem(item, recurrenceMode));
            }
        });
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.Recurrence", "\uE823", recurrenceMenu, flyout, item.DueDate is not null));

        if (item.DueDate is not null &&
            !item.IsCompleted &&
            !TodoReminderOptions.IsReminderOff(item.ReminderOffsetMinutes))
        {
            var snoozeMenu = CreateLazyTodoMenu(flyout, menu =>
            {
                menu.Items.Add(CreateSnoozeItem(item, localization.T("Todo.Snooze.10Minutes"), TimeSpan.FromMinutes(10)));
                menu.Items.Add(CreateSnoozeItem(item, localization.T("Todo.Snooze.30Minutes"), TimeSpan.FromMinutes(30)));
                menu.Items.Add(CreateSnoozeItem(item, localization.T("Todo.Snooze.OneHour"), TimeSpan.FromHours(1)));
                menu.Items.Add(CreateSnoozeItem(item, localization.T("Todo.Snooze.Tomorrow"), GetTomorrowSnoozeTime()));
            });
            flyout.Items.Add(CreateTodoContextSubmenu(
                "Todo.Menu.Snooze", "\uE823", snoozeMenu, flyout));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private static MenuFlyout CreateLazyTodoMenu(
        MenuFlyout owner,
        Action<MenuFlyout> populate)
    {
        var menu = new MenuFlyout();
        populate(menu);
        foreach (var menuItem in menu.Items.OfType<MenuFlyoutItem>())
        {
            menuItem.Click += (_, _) => owner.Hide();
        }
        return menu;
    }

    private MenuFlyout CreateMultiItemFlyout(
        IReadOnlyList<TodoItemViewModel> selectedItems,
        FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        var localization = App.Current.LocalizationService;
        string[] selectedIds = selectedItems.Select(item => item.Id).ToArray();

        var copyItem = new MenuFlyoutItem
        {
            Text = localization.Format("Todo.Menu.CopySelected", selectedItems.Count),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += (_, _) =>
        {
            flyout.Hide();
            CopySelectedTodoItems(selectedItems);
        };
        flyout.Items.Add(copyItem);

        var deleteItem = CreateTodoContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += (_, _) =>
        {
            flyout.Hide();
            DispatcherQueue.TryEnqueue(() => ShowDeleteSelectedConfirmation(selectedIds, anchor));
        };
        flyout.Items.Add(new MenuFlyoutSeparator());

        var colorMenu = new MenuFlyout();
        colorMenu.Items.Add(CreateBatchColorMarkerItem(selectedItems, selectedIds, null, flyout));
        colorMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (string colorMarker in TodoItem.SupportedColorMarkers)
        {
            colorMenu.Items.Add(CreateBatchColorMarkerItem(selectedItems, selectedIds, colorMarker, flyout));
        }
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.ColorMarker", "\uE790", colorMenu, flyout));

        bool hasDueDate = selectedItems.Any(item => item.DueDate is not null);
        var dueDateMenu = new MenuFlyout();
        dueDateMenu.Items.Add(CreateBatchDuePresetItem(
            selectedIds, TodoDuePreset.Today, localization.T("Todo.Due.Today"), flyout));
        dueDateMenu.Items.Add(CreateBatchDuePresetItem(
            selectedIds, TodoDuePreset.Tomorrow, localization.T("Todo.Due.Tomorrow"), flyout));
        dueDateMenu.Items.Add(CreateBatchDuePresetItem(
            selectedIds, TodoDuePreset.ThisWeek, localization.T("Todo.Due.ThisWeek"), flyout));
        dueDateMenu.Items.Add(CreateBatchDuePresetItem(
            selectedIds, TodoDuePreset.NextMonday, localization.T("Todo.Due.NextMonday"), flyout));
        dueDateMenu.Items.Add(new MenuFlyoutSeparator());
        var customDueDateItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Due.Custom"),
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        customDueDateItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await PickBatchCustomDueDateAsync(
                selectedIds,
                selectedItems.FirstOrDefault(item => item.DueDate is not null)?.DueDate);
        };
        dueDateMenu.Items.Add(customDueDateItem);
        if (hasDueDate)
        {
            dueDateMenu.Items.Add(CreateBatchDuePresetItem(
                selectedIds, TodoDuePreset.Clear, localization.T("Todo.Due.Clear"), flyout));
        }
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.DueDate", "\uE787", dueDateMenu, flyout));

        var reminderMenu = new MenuFlyout();
        reminderMenu.Items.Add(CreateBatchReminderOffsetItem(selectedItems, selectedIds, null, flyout));
        reminderMenu.Items.Add(CreateBatchReminderOffsetItem(
            selectedItems,
            selectedIds,
            TodoReminderOptions.ReminderOff,
            flyout));
        reminderMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (int offsetMinutes in TodoReminderOptions.SupportedOffsetMinutes)
        {
            reminderMenu.Items.Add(CreateBatchReminderOffsetItem(
                selectedItems,
                selectedIds,
                offsetMinutes,
                flyout));
        }
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.Reminder", "\uEA8F", reminderMenu, flyout, hasDueDate));

        var recurrenceMenu = new MenuFlyout();
        foreach (string recurrenceMode in TodoRecurrenceMode.SupportedModes)
        {
            recurrenceMenu.Items.Add(CreateBatchRecurrenceItem(
                selectedItems, selectedIds, recurrenceMode, flyout));
        }
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.Recurrence", "\uE823", recurrenceMenu, flyout, hasDueDate));

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private MenuFlyoutItem CreateBatchColorMarkerItem(
        IReadOnlyList<TodoItemViewModel> selectedItems,
        IReadOnlyList<string> selectedIds,
        string? colorMarker,
        MenuFlyout owner)
    {
        string? normalizedColorMarker = TodoItem.NormalizeColorMarker(colorMarker);
        bool isSelected = selectedItems.All(item =>
            string.Equals(item.ColorMarker, normalizedColorMarker, StringComparison.Ordinal));
        var menuItem = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T(TodoItem.GetColorMarkerLocalizationKey(colorMarker)),
            Icon = colorMarker is null
                ? new FontIcon { Glyph = isSelected ? "\uE73E" : "\uE711" }
                : CreateColorMarkerIcon(colorMarker, isSelected)
        };
        menuItem.Click += async (_, _) =>
        {
            owner.Hide();
            if (ViewModel is not null)
            {
                await ViewModel.SetColorMarkerAsync(selectedIds, colorMarker);
            }
        };
        return menuItem;
    }

    private MenuFlyoutItem CreateBatchReminderOffsetItem(
        IReadOnlyList<TodoItemViewModel> selectedItems,
        IReadOnlyList<string> selectedIds,
        int? offsetMinutes,
        MenuFlyout owner)
    {
        int? normalizedOffset = TodoReminderOptions.NormalizeOffsetMinutes(offsetMinutes);
        var dueItems = selectedItems.Where(item => item.DueDate is not null).ToList();
        bool isSelected = dueItems.Count > 0 &&
                          dueItems.All(item => Nullable.Equals(item.ReminderOffsetMinutes, normalizedOffset));
        var menuItem = new MenuFlyoutItem
        {
            Text = FormatReminderOffsetText(normalizedOffset),
            Icon = isSelected ? new FontIcon { Glyph = "\uE73E" } : null
        };
        menuItem.Click += async (_, _) =>
        {
            owner.Hide();
            if (ViewModel is not null)
            {
                await ViewModel.SetReminderOffsetAsync(selectedIds, normalizedOffset);
            }
        };
        return menuItem;
    }

    private MenuFlyoutItem CreateBatchDuePresetItem(
        IReadOnlyList<string> selectedIds,
        TodoDuePreset preset,
        string text,
        MenuFlyout owner)
    {
        var menuItem = new MenuFlyoutItem { Text = text };
        menuItem.Click += async (_, _) =>
        {
            owner.Hide();
            if (ViewModel is not null)
            {
                await ViewModel.SetDueDatePresetAsync(selectedIds, preset);
            }
        };
        return menuItem;
    }

    private MenuFlyoutItem CreateBatchRecurrenceItem(
        IReadOnlyList<TodoItemViewModel> selectedItems,
        IReadOnlyList<string> selectedIds,
        string recurrenceMode,
        MenuFlyout owner)
    {
        string normalizedMode = TodoRecurrenceMode.Normalize(recurrenceMode);
        var dueItems = selectedItems.Where(item => item.DueDate is not null).ToList();
        bool isSelected = dueItems.Count > 0 &&
                          dueItems.All(item => string.Equals(item.RecurrenceMode, normalizedMode, StringComparison.Ordinal));
        var menuItem = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T(TodoRecurrenceService.GetLocalizationKey(normalizedMode)),
            Icon = isSelected ? new FontIcon { Glyph = "\uE73E" } : null
        };
        menuItem.Click += async (_, _) =>
        {
            owner.Hide();
            if (ViewModel is not null)
            {
                await ViewModel.SetRecurrenceAsync(selectedIds, normalizedMode);
            }
        };
        return menuItem;
    }

    private MenuFlyoutItem CreateTodoContextCommand(string localizationKey, string glyph)
    {
        return new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T(localizationKey),
            Icon = new FontIcon { Glyph = glyph }
        };
    }

    private MenuFlyoutSubItem CreateTodoContextSubmenu(
        string localizationKey,
        string glyph,
        MenuFlyout submenu,
        MenuFlyout owner,
        bool isEnabled = true)
    {
        foreach (var menuItem in submenu.Items.OfType<MenuFlyoutItem>())
        {
            menuItem.Click += (_, _) => owner.Hide();
        }

        var subItem = new MenuFlyoutSubItem
        {
            Text = App.Current.LocalizationService.T(localizationKey),
            Icon = new FontIcon { Glyph = glyph },
            IsEnabled = isEnabled
        };
        while (submenu.Items.Count > 0)
        {
            MenuFlyoutItemBase item = submenu.Items[0];
            submenu.Items.RemoveAt(0);
            subItem.Items.Add(item);
        }

        return subItem;
    }

    private MenuFlyoutItem CreateColorMarkerItem(TodoItemViewModel item, string? colorMarker)
    {
        var localization = App.Current.LocalizationService;
        bool isSelected = string.Equals(
            item.ColorMarker,
            TodoItem.NormalizeColorMarker(colorMarker),
            StringComparison.Ordinal);
        var colorItem = new MenuFlyoutItem
        {
            Text = localization.T(TodoItem.GetColorMarkerLocalizationKey(colorMarker)),
            Icon = colorMarker is null
                ? new FontIcon { Glyph = isSelected ? "\uE73E" : "\uE711" }
                : CreateColorMarkerIcon(colorMarker, isSelected)
        };
        colorItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetColorMarkerAsync(item.Id, colorMarker);
            }
        };

        return colorItem;
    }

    private static IconElement CreateColorMarkerIcon(string colorMarker, bool isSelected)
    {
        var color = ParseColor(TodoItem.GetColorMarkerHex(colorMarker));
        return new FontIcon
        {
            Glyph = isSelected ? "\uE73E" : "\u25CF",
            FontFamily = isSelected ? new FontFamily("Segoe MDL2 Assets") : new FontFamily("Segoe UI Symbol"),
            FontSize = isSelected ? 12 : 10,
            Foreground = new SolidColorBrush(color)
        };
    }

    private MenuFlyoutItem CreateDuePresetItem(TodoItemViewModel item, TodoDuePreset preset, string text)
    {
        var dueItem = new MenuFlyoutItem
        {
            Text = text
        };
        dueItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetDueDatePresetAsync(item.Id, preset);
            }
        };
        return dueItem;
    }

    private MenuFlyoutItem CreateRecurrenceItem(TodoItemViewModel item, string recurrenceMode)
    {
        string normalizedMode = TodoRecurrenceMode.Normalize(recurrenceMode);
        bool isSelected = string.Equals(item.RecurrenceMode, normalizedMode, StringComparison.Ordinal);
        var recurrenceItem = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T(TodoRecurrenceService.GetLocalizationKey(normalizedMode)),
            Icon = isSelected
                ? new FontIcon { Glyph = "\uE73E" }
                : null
        };
        recurrenceItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetRecurrenceAsync(item.Id, normalizedMode);
            }
        };
        return recurrenceItem;
    }

    private MenuFlyoutItem CreateReminderOffsetItem(TodoItemViewModel item, int? offsetMinutes)
    {
        int? normalizedOffset = TodoReminderOptions.NormalizeOffsetMinutes(offsetMinutes);
        bool isSelected = Nullable.Equals(item.ReminderOffsetMinutes, normalizedOffset);
        var reminderItem = new MenuFlyoutItem
        {
            Text = FormatReminderOffsetText(normalizedOffset),
            Icon = isSelected
                ? new FontIcon { Glyph = "\uE73E" }
                : null
        };
        reminderItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetReminderOffsetAsync(item.Id, normalizedOffset);
            }
        };
        return reminderItem;
    }

    private MenuFlyoutItem CreateSnoozeItem(TodoItemViewModel item, string text, TimeSpan snoozeFor)
    {
        var snoozeItem = new MenuFlyoutItem
        {
            Text = text
        };
        snoozeItem.Click += async (_, _) =>
        {
            if (ViewModel is not null &&
                await ViewModel.SnoozeReminderAsync(item.Id, snoozeFor))
            {
                ShowUndoToast(
                    App.Current.LocalizationService.Format("Todo.Snooze.Set", text),
                    durationMs: CopyToastMs,
                    clearUndoOnHide: false);
            }
        };
        return snoozeItem;
    }

    private MenuFlyoutItem CreateSnoozeItem(TodoItemViewModel item, string text, DateTimeOffset snoozedUntil)
    {
        var snoozeItem = new MenuFlyoutItem
        {
            Text = text
        };
        snoozeItem.Click += async (_, _) =>
        {
            if (ViewModel is not null &&
                await ViewModel.SnoozeReminderUntilAsync(item.Id, snoozedUntil))
            {
                ShowUndoToast(
                    App.Current.LocalizationService.Format("Todo.Snooze.Set", text),
                    durationMs: CopyToastMs,
                    clearUndoOnHide: false);
            }
        };
        return snoozeItem;
    }

    private static DateTimeOffset GetTomorrowSnoozeTime()
    {
        DateTime tomorrowMorning = DateTime.Now.Date.AddDays(1).AddHours(9);
        return new DateTimeOffset(tomorrowMorning);
    }

    private static string FormatReminderOffsetText(int? offsetMinutes)
    {
        var localization = App.Current.LocalizationService;
        if (offsetMinutes is null)
        {
            int defaultOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(
                App.Current.SettingsService.Settings.TodoDefaultReminderOffsetMinutes);
            return localization.Format(
                "Todo.Reminder.Default",
                FormatExplicitReminderOffsetText(defaultOffsetMinutes));
        }

        if (TodoReminderOptions.IsReminderOff(offsetMinutes))
        {
            return localization.T("Todo.Reminder.Off");
        }

        return FormatExplicitReminderOffsetText(offsetMinutes.Value);
    }

    private static string FormatExplicitReminderOffsetText(int offsetMinutes)
    {
        var localization = App.Current.LocalizationService;
        return SettingsService.NormalizeTodoReminderOffsetMinutes(offsetMinutes) switch
        {
            0 => localization.T("Todo.Reminder.AtDueTime"),
            60 => localization.T("Todo.Reminder.OneHourBefore"),
            1440 => localization.T("Todo.Reminder.OneDayBefore"),
            int minutes => localization.Format("Todo.Reminder.MinutesBefore", minutes)
        };
    }

    private void CopyTodoItemText(TodoItemViewModel item)
    {
        string text = TodoClipboardFormatter.FormatSingle(item, App.Current.LocalizationService);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            SetClipboardText(text);
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.Copied"),
                durationMs: CopyToastMs,
                clearUndoOnHide: false);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to copy item {item.Id}: {ex}");
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.CopyFailed"),
                durationMs: UndoToastMs,
                clearUndoOnHide: false);
        }
    }

    private void CopySelectedTodoItems()
    {
        CopySelectedTodoItems(GetSelectedCopyItemsInVisibleOrder());
    }

    private void CopySelectedTodoItems(IReadOnlyList<TodoItemViewModel> selectedItems)
    {
        if (selectedItems.Count == 0)
        {
            return;
        }

        if (selectedItems.Count == 1)
        {
            CopyTodoItemText(selectedItems[0]);
            return;
        }

        var localization = App.Current.LocalizationService;
        string text = TodoClipboardFormatter.FormatBatch(selectedItems, localization);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            SetClipboardText(text);
            ShowUndoToast(
                localization.Format("Todo.CopiedCount", selectedItems.Count),
                durationMs: CopyToastMs,
                clearUndoOnHide: false);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to copy {selectedItems.Count} selected items: {ex}");
            ShowUndoToast(
                localization.T("Todo.CopyFailed"),
                durationMs: UndoToastMs,
                clearUndoOnHide: false);
        }
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

    private void ToggleTodoSelection(TodoItemViewModel item)
    {
        item.IsCopySelected = !item.IsCopySelected;
        _copySelectionAnchorId = item.Id;
    }

    private void SelectTodoRange(TodoItemViewModel item)
    {
        if (ViewModel is null || ViewModel.VisibleItems.Count == 0)
        {
            return;
        }

        int endIndex = ViewModel.VisibleItems.IndexOf(item);
        if (endIndex < 0)
        {
            return;
        }

        int startIndex = endIndex;
        if (!string.IsNullOrWhiteSpace(_copySelectionAnchorId))
        {
            for (int index = 0; index < ViewModel.VisibleItems.Count; index++)
            {
                if (string.Equals(ViewModel.VisibleItems[index].Id, _copySelectionAnchorId, StringComparison.Ordinal))
                {
                    startIndex = index;
                    break;
                }
            }
        }

        int first = Math.Min(startIndex, endIndex);
        int last = Math.Max(startIndex, endIndex);
        ClearCopySelection();
        for (int index = first; index <= last; index++)
        {
            ViewModel.VisibleItems[index].IsCopySelected = true;
        }

        _copySelectionAnchorId = ViewModel.VisibleItems[startIndex].Id;
    }

    private void SelectAllVisibleTodoItems()
    {
        if (ViewModel is null)
        {
            return;
        }

        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = false;
        }

        foreach (var item in ViewModel.VisibleItems)
        {
            item.IsCopySelected = true;
        }

        _copySelectionAnchorId = ViewModel.VisibleItems.FirstOrDefault()?.Id;
    }

    private bool CanStartTodoBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return FindTodoItemContainer(source) is null &&
               !HasAncestorOfType<ScrollBar>(source) &&
               !HasAncestorOfType<ButtonBase>(source) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private void UpdateTodoSelectionRectangleVisual()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        Canvas.SetLeft(TodoSelectionRectangle, selectionRect.X);
        Canvas.SetTop(TodoSelectionRectangle, selectionRect.Y);
        TodoSelectionRectangle.Width = Math.Max(0, selectionRect.Width);
        TodoSelectionRectangle.Height = Math.Max(0, selectionRect.Height);
        TodoSelectionRectangle.Visibility = selectionRect.Width > 0 && selectionRect.Height > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CacheTodoSelectionHitTestItems(ListViewBase listView)
    {
        _selectionHitTestItems = [];

        if (ViewModel is null)
        {
            return;
        }

        foreach (var item in ViewModel.VisibleItems)
        {
            if (listView.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            var target = FindVisualChild<Grid>(container, "TodoItemRoot") ?? container as FrameworkElement;
            if (target is null || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = target.TransformToVisual(TodoSelectionOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var bounds = new Windows.Foundation.Rect(
                topLeft.X,
                topLeft.Y,
                target.ActualWidth,
                target.ActualHeight);
            _selectionHitTestItems.Add(new TodoSelectionHitTestItem(item, bounds));
        }
    }

    private void ApplyTodoSelectionRectanglePreview()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        var selectedItems = new HashSet<TodoItemViewModel>(_selectionSnapshot);

        foreach (var hitTestItem in _selectionHitTestItems)
        {
            if (RectsIntersect(selectionRect, hitTestItem.Bounds))
            {
                selectedItems.Add(hitTestItem.Item);
            }
        }

        ApplyTodoSelectionPreview(selectedItems);
    }

    private void FinishTodoSelectionRectangle(ListViewBase listView)
    {
        bool shouldHandle = _selectionPointerPressed || _isBoxSelecting;
        _selectionPointerPressed = false;

        if (_isBoxSelecting)
        {
            ApplyTodoSelectionRectanglePreview();
            _copySelectionAnchorId = ViewModel?.VisibleItems.FirstOrDefault(item => item.IsCopySelected)?.Id;
        }

        _isBoxSelecting = false;
        _selectionSnapshot = [];
        _selectionPreviewItems = [];
        _selectionHitTestItems = [];
        TodoSelectionRectangle.Visibility = Visibility.Collapsed;
        TodoSelectionRectangle.Width = 0;
        TodoSelectionRectangle.Height = 0;

        if (shouldHandle)
        {
            listView.Focus(FocusState.Programmatic);
        }
    }

    private void ApplyTodoSelectionPreview(HashSet<TodoItemViewModel> selectedItems)
    {
        if (ViewModel is null)
        {
            return;
        }

        _selectionPreviewItems = selectedItems;
        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = selectedItems.Contains(item);
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

    private void ClearCopySelection()
    {
        if (ViewModel is null)
        {
            _copySelectionAnchorId = null;
            return;
        }

        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = false;
        }

        _copySelectionAnchorId = null;
    }

    private bool HasCopySelection()
    {
        return ViewModel?.Items.Any(item => item.IsCopySelected) == true;
    }

    private IReadOnlyList<TodoItemViewModel> GetSelectedCopyItemsInVisibleOrder()
    {
        if (ViewModel is null)
        {
            return [];
        }

        return ViewModel.VisibleItems
            .Where(item => item.IsCopySelected)
            .ToList();
    }

        private Task PickCustomDueDateAsync(TodoItemViewModel? item)
    {
        if (ViewModel is null)
        {
            return Task.CompletedTask;
        }

        CloseTodoEdit();
        _pendingConfirmFlyout?.Hide();
        _customDueDateItem = item;
        _customDueDateItemIds = null;
        DateTimeOffset dueDate = item?.DueDate ?? ViewModel.DraftDueDate ?? GetDefaultCustomDueDate();
        CustomDueDatePicker.MinDate = DateTimeOffset.Now.Date;
        CustomDueDatePicker.Date = dueDate;
        SetCustomDueTime(dueDate);
        ApplyLocalizedText();
        ApplyEditorVisualStyle();
        CustomDueDateOverlay.Visibility = Visibility.Visible;
        CustomDueDatePicker.Focus(FocusState.Programmatic);
        return Task.CompletedTask;
    }

    private Task PickBatchCustomDueDateAsync(
        IReadOnlyList<string> itemIds,
        DateTimeOffset? initialDueDate)
    {
        if (ViewModel is null || itemIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        CloseTodoEdit();
        _pendingConfirmFlyout?.Hide();
        _customDueDateItem = null;
        _customDueDateItemIds = itemIds.ToArray();
        DateTimeOffset dueDate = initialDueDate ?? GetDefaultCustomDueDate();
        CustomDueDatePicker.MinDate = DateTimeOffset.Now.Date;
        CustomDueDatePicker.Date = dueDate;
        SetCustomDueTime(dueDate);
        ApplyLocalizedText();
        ApplyEditorVisualStyle();
        CustomDueDateOverlay.Visibility = Visibility.Visible;
        CustomDueDatePicker.Focus(FocusState.Programmatic);
        return Task.CompletedTask;
    }

    private void CustomDueDateCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseCustomDueDateOverlay();
    }

    private void CustomDueTimeButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new TimePickerFlyout
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 1,
            Time = _customDueTime
        };
        flyout.TimePicked += (_, args) =>
        {
            _customDueTime = args.NewTime;
            UpdateCustomDueTimeText();
        };

        flyout.ShowAt(CustomDueTimeButton, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.Top
        });
    }

        private async void CustomDueDateSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            CloseCustomDueDateOverlay();
            return;
        }

        DateTimeOffset selectedDate = CustomDueDatePicker.Date ?? DateTimeOffset.Now;
        DateTimeOffset selectedDueDate = CombineCustomDueDateAndTime(selectedDate);
        TodoItemViewModel? customDueDateItem = _customDueDateItem;
        IReadOnlyList<string>? customDueDateItemIds = _customDueDateItemIds;
        CloseCustomDueDateOverlay();

        if (customDueDateItem is { } item)
        {
            await ViewModel.SetDueDateAsync(item.Id, selectedDueDate);
        }
        else if (customDueDateItemIds is { Count: > 0 } itemIds)
        {
            await ViewModel.SetDueDateAsync(itemIds, selectedDueDate);
        }
        else
        {
            ViewModel.DraftDueDate = selectedDueDate;
        }
    }

    private void CloseCustomDueDateOverlay()
    {
        _customDueDateItem = null;
        _customDueDateItemIds = null;
        if (CustomDueDateOverlay is not null)
        {
            CustomDueDateOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SetCustomDueTime(DateTimeOffset dueDate)
    {
        DateTimeOffset localDueDate = dueDate.ToLocalTime();
        _customDueTime = new TimeSpan(localDueDate.Hour, localDueDate.Minute, 0);
        UpdateCustomDueTimeText();
    }

    private void UpdateCustomDueTimeText()
    {
        if (CustomDueTimeText is not null)
        {
            CustomDueTimeText.Text = $"{_customDueTime.Hours:00}:{_customDueTime.Minutes:00}";
        }
    }

    private DateTimeOffset CombineCustomDueDateAndTime(DateTimeOffset selectedDate)
    {
        DateTimeOffset localDate = selectedDate.ToLocalTime();
        var localDateTime = new DateTime(
            localDate.Year,
            localDate.Month,
            localDate.Day,
            _customDueTime.Hours,
            _customDueTime.Minutes,
            0,
            DateTimeKind.Local);

        return new DateTimeOffset(localDateTime);
    }

    private static DateTimeOffset GetDefaultCustomDueDate()
    {
        DateTime today = DateTime.Now.Date;
        return new DateTimeOffset(new DateTime(today.Year, today.Month, today.Day, 23, 59, 0, DateTimeKind.Local));
    }

    private async Task DeleteItemAsync(TodoItemViewModel item, FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.ConfirmBeforeDelete)
        {
            ShowTodoConfirmMenu(
                anchor,
                App.Current.LocalizationService.T("Todo.DeleteConfirm.Title"),
                App.Current.LocalizationService.T("Common.Delete"),
                async () => await ViewModel.DeleteItemAsync(item.Id));
            return;
        }

        await ViewModel.DeleteItemAsync(item.Id);
    }

    private void ShowDeleteItemConfirmation(TodoItemViewModel item, FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!ViewModel.ConfirmBeforeDelete)
        {
            _ = ViewModel.DeleteItemAsync(item.Id);
            return;
        }

        ShowTodoConfirmMenu(
            anchor,
            App.Current.LocalizationService.T("Todo.DeleteConfirm.Title"),
            App.Current.LocalizationService.T("Common.Delete"),
            async () => await ViewModel.DeleteItemAsync(item.Id));
    }

    private void ShowDeleteSelectedConfirmation(IReadOnlyList<string> selectedIds, FrameworkElement anchor)
    {
        if (ViewModel is null || selectedIds.Count == 0)
        {
            return;
        }

        async Task DeleteSelectedAsync()
        {
            ClearCopySelection();
            await ViewModel.DeleteItemsAsync(selectedIds);
        }

        if (!ViewModel.ConfirmBeforeDelete)
        {
            _ = DeleteSelectedAsync();
            return;
        }

        ShowTodoConfirmMenu(
            anchor,
            App.Current.LocalizationService.Format("Todo.DeleteSelectedConfirm.Title", selectedIds.Count),
            App.Current.LocalizationService.T("Common.Delete"),
            DeleteSelectedAsync);
    }

        private void BeginItemEdit(TodoItemViewModel item)
    {
        ClearCopySelection();
        CloseCustomDueDateOverlay();

        if (!item.IsExpanded && ViewModel is not null)
        {
            ViewModel.ToggleExpanded(item.Id);
            TodoListView.ScrollIntoView(item);
        }

        ViewModel?.BeginEdit(item.Id);
    }

    private async void TodoEditSaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveTodoEditAsync();
    }

    private void TodoEditCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseTodoEdit();
    }

    private async void TodoEditTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CloseTodoEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter &&
            Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            await SaveTodoEditAsync();
            e.Handled = true;
        }
    }

    private async Task SaveTodoEditAsync()
    {
        if (ViewModel is null)
        {
            CloseTodoEdit();
            return;
        }

        if (_editingItem is not { } item)
        {
            CloseTodoEdit();
            return;
        }

        bool updated = await ViewModel.UpdateItemTextAsync(item.Id, TodoInlineEditor.Text);
        if (!updated)
        {
            TodoEditTextBox.Focus(FocusState.Programmatic);
            TodoEditTextBox.SelectAll();
            return;
        }

        CloseTodoEdit();
    }

    private void CloseTodoEdit()
    {
        _editingItem = null;
        if (TodoInlineEditor is null)
        {
            return;
        }

        TodoInlineEditor.Visibility = Visibility.Collapsed;
        TodoInlineEditor.Text = string.Empty;
        TodoInlineEditor.Title = App.Current.LocalizationService.T("Todo.Menu.Edit");
    }

    private static void SetTodoItemHoverState(DependencyObject? itemRoot, bool isHovered)
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

        if (FindVisualChild<Border>(itemRoot, "TodoItemHoverBackground") is { } hoverBackgroundBorder)
        {
            hoverBackgroundBorder.Background = new SolidColorBrush(hoverBackground);
            hoverBackgroundBorder.Opacity = isHovered ? 1 : 0;
        }

        if (FindVisualChild<Border>(itemRoot, "TodoItemActionHost") is { } actions)
        {
            actions.Opacity = isHovered ? 1 : 0;
            actions.IsHitTestVisible = isHovered;
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

        bool isCompleted = itemRoot is FrameworkElement { DataContext: TodoItemViewModel { IsCompleted: true } };
        if (FindVisualChild<Border>(itemRoot, "TodoCompletionBox") is { } completionBox)
        {
            completionBox.Background = isCompleted
                ? new SolidColorBrush(WithAlpha(accentColor, 0xFF))
                : new SolidColorBrush(Colors.Transparent);
            completionBox.BorderBrush = isCompleted
                ? new SolidColorBrush(WithAlpha(accentColor, 0xFF))
                : new SolidColorBrush(isHovered
                    ? WithAlpha(accentColor, isDark ? (byte)0xFF : (byte)0xE8)
                    : isDark
                        ? WithAlpha(accentColor, 0xD8)
                        : WithAlpha(accentColor, 0xB8));
        }
    }

    private void ApplyEditorVisualStyle()
    {
        if (TodoInlineEditor is null)
        {
            return;
        }

        bool isDark = ActualTheme == ElementTheme.Dark;
        TodoInlineEditor.OverlaySurface.Background = new SolidColorBrush(GetNeutralOverlaySurfaceColor(isDark));
        TodoInlineEditor.OverlaySurface.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        TodoInlineEditor.OverlaySurface.BorderThickness = new Thickness(0.8);
        TodoEditTextBox.Background = new SolidColorBrush(GetNeutralInputSurfaceColor(isDark));
        TodoEditTextBox.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        TodoEditTextBox.Foreground = GetBrushResourceOrFallback(
            "TextFillColorPrimaryBrush",
            isDark ? Colors.White : Colors.Black);

        CustomDueDateOverlay.Background = new SolidColorBrush(GetNeutralOverlaySurfaceColor(isDark));
        CustomDueDateOverlay.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        CustomDueDateOverlay.BorderThickness = new Thickness(0.8);
        ApplyDetailCompletionVisualState();
    }

    private void ApplyDetailCompletionVisualState()
    {
        if (DetailCompletionBox is null || ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        bool isDark = ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        DetailCompletionBox.Background = item.IsCompleted
            ? new SolidColorBrush(accentColor)
            : new SolidColorBrush(Colors.Transparent);
        DetailCompletionBox.BorderBrush = item.IsCompleted
            ? new SolidColorBrush(Colors.Transparent)
            : new SolidColorBrush(isDark
                ? WithAlpha(accentColor, 0xD8)
                : WithAlpha(accentColor, 0xB8));
    }

    private void ApplySelectionRectangleStyle()
    {
        if (TodoSelectionRectangle is null)
        {
            return;
        }

        bool isDark = ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        TodoSelectionRectangle.Background = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x2D : (byte)0x24));
        TodoSelectionRectangle.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC));
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

    private static Windows.UI.Color ParseColor(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length != 6 ||
            !byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out byte red) ||
            !byte.TryParse(value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte green) ||
            !byte.TryParse(value.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte blue))
        {
            return Colors.Gray;
        }

        return ColorHelper.FromArgb(0xFF, red, green, blue);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null)
        where T : FrameworkElement
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T typed &&
                (name is null || string.Equals(typed.Name, name, StringComparison.Ordinal)))
            {
                return typed;
            }

            if (FindVisualChild<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : FrameworkElement
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static FrameworkElement? FindTodoItemContainer(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Grid grid &&
                grid.DataContext is TodoItemViewModel)
            {
                return grid;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool HasAncestorOfType<T>(DependencyObject source) where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement anchor)
        {
            return;
        }

        if (ViewModel.ConfirmBeforeDelete)
        {
            ShowTodoConfirmMenu(
                anchor,
                App.Current.LocalizationService.T("Todo.ClearCompletedConfirm.Title"),
                App.Current.LocalizationService.T("Todo.ClearCompleted"),
                async () => await ViewModel.ClearCompletedAsync());
            return;
        }

        await ViewModel.ClearCompletedAsync();
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.UndoLastActionAsync();
            ShowUndoToast(App.Current.LocalizationService.T("Common.Undone"));
        }
    }

    private void DismissUndoButton_Click(object sender, RoutedEventArgs e)
    {
        HideUndoToast(clearUndo: true);
    }

    private void ShowUndoToast(
        string text,
        string? actionText = null,
        int durationMs = UndoToastMs,
        bool clearUndoOnHide = true)
    {
        long generation = ++_undoToastGeneration;
        UndoToastText.Text = text;
        UndoToastActionButton.Content = actionText;
        UndoToastActionButton.Visibility = string.IsNullOrWhiteSpace(actionText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UndoToast.IsHitTestVisible = !string.IsNullOrWhiteSpace(actionText);
        UndoToast.Opacity = 1;
        _ = HideUndoToastAfterDelayAsync(generation, durationMs, clearUndoOnHide);
    }

    private async Task HideUndoToastAfterDelayAsync(long generation, int durationMs, bool clearUndo)
    {
        await Task.Delay(durationMs);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == _undoToastGeneration)
            {
                HideUndoToast(clearUndo);
            }
        });
    }

    private void HideUndoToast(bool clearUndo)
    {
        _undoToastGeneration++;
        UndoToast.Opacity = 0;
        UndoToast.IsHitTestVisible = false;
        UndoToastActionButton.Visibility = Visibility.Collapsed;
        if (clearUndo)
        {
            ViewModel?.DismissUndo();
        }
    }

    public void ShowClearAllConfirmation(FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.ConfirmBeforeDelete)
        {
            ShowTodoConfirmMenu(
                anchor,
                App.Current.LocalizationService.T("Todo.ClearAllConfirm.Title"),
                App.Current.LocalizationService.T("Todo.ClearAll"),
                async () => await ViewModel.ClearAllAsync());
            return;
        }

        _ = ViewModel.ClearAllAsync();
    }

    private void ShowTodoConfirmMenu(
        FrameworkElement anchor,
        string title,
        string actionText,
        Func<Task> confirmedAction)
    {
        _pendingConfirmFlyout?.Hide();

        var flyout = WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
            title,
            actionText,
            confirmedAction);
        _pendingConfirmFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_pendingConfirmFlyout, flyout))
            {
                _pendingConfirmFlyout = null;
            }
        };
        flyout.ShowAt(anchor);
    }

    private static string GetDeleteConfirmPreviewText(string text)
    {
        string normalized = string.Join(
            " ",
            (text ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "Task";
        }

        return normalized.Length <= 34 ? normalized : $"{normalized[..34].Trim()}...";
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
            _ = await ViewModel.CommitEditAsync(item.Id);
            e.Handled = true;
        }
    }

    private async void DetailBackButton_Click(object sender, RoutedEventArgs e)
    {
        await CloseDetailAsync();
    }

    private async Task CloseDetailAsync()
    {
        if (ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        TodoItemViewModel? finalizedItem = await ViewModel.FinalizeDetailAsync(DetailTitleTextBox.Text);
        ClearTodoListContainerSelection();
        Focus(FocusState.Programmatic);
        if (finalizedItem is not null)
        {
            TodoListView.ScrollIntoView(finalizedItem);
        }
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

        if (e.Key == VirtualKey.Enter && Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            if (ViewModel?.SelectedDetailItem is { } item)
            {
                await ViewModel.UpdateItemTextAsync(item.Id, DetailTitleTextBox.Text);
            }

            e.Handled = true;
        }
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

    private async void DetailAddFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedDetailItem is not { } item)
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
            InitializeWithWindow.Initialize(picker, owner == IntPtr.Zero ? foreground : owner);

            IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
            foreach (StorageFile file in files)
            {
                await ViewModel.AddAttachmentPathAsync(item.Id, file.Path);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Add attachment failed: {ex}");
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
            if (File.Exists(attachment.FilePath))
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
                await Launcher.LaunchFileAsync(file);
                return;
            }

            if (Directory.Exists(attachment.FilePath))
            {
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(attachment.FilePath);
                await Launcher.LaunchFolderAsync(folder);
                return;
            }

            ShowUndoToast(
                ViewModel?.DetailFileMissingText ?? string.Empty,
                durationMs: CopyToastMs,
                clearUndoOnHide: false);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Open attachment failed: {ex}");
        }
    }

    private async void TodoAttachmentPreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            await attachment.EnsureThumbnailAsync();
        }
    }

    private async void DetailRemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment } ||
            ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        await ViewModel.DeleteAttachmentAsync(item.Id, attachment.Id);
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
