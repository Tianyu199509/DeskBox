using DeskBox.Controls;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private readonly DispatcherTimer _notesAutosaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(600)
    };
    private readonly MarkdownDocumentService _markdownDocumentService = new();
    private readonly SemaphoreSlim _notesSaveGate = new(1, 1);
    private string? _notesEditingItemId;
    private string? _inFlightNotesSaveItemId;
    private string? _inFlightNotesSaveSource;
    private string _notesOriginalText = string.Empty;
    private bool _notesExitRequested;
    private DoubleTappedEventHandler? _detailNotesReaderDoubleTappedHandler;

    private void InitializeDetailNotesAndSteps()
    {
        if (DetailPage is null)
        {
            return;
        }

        DetailNotesView.AttachmentResolver = ResolveSelectedTodoAttachment;
        DetailNotesView.AttachmentOpenRequested += DetailNotesView_AttachmentOpenRequested;
        _detailNotesReaderDoubleTappedHandler = new DoubleTappedEventHandler(DetailNotesReaderHost_DoubleTapped);
        DetailNotesReaderHost.AddHandler(
            UIElement.DoubleTappedEvent,
            _detailNotesReaderDoubleTappedHandler,
            handledEventsToo: true);
        ApplyNotesEditingVisualState(isEditing: false);
    }

    private void ReleaseDetailNotesAndStepsVisualHandlers()
    {
        if (DetailPage is null)
        {
            return;
        }

        if (_detailNotesReaderDoubleTappedHandler is not null)
        {
            DetailNotesReaderHost.RemoveHandler(
                UIElement.DoubleTappedEvent,
                _detailNotesReaderDoubleTappedHandler);
            _detailNotesReaderDoubleTappedHandler = null;
        }

        DetailNotesView.AttachmentOpenRequested -= DetailNotesView_AttachmentOpenRequested;
        DetailNotesView.AttachmentResolver = null;
        DetailNotesView.ReleaseVisualHandlers();
        DetailNotesEditor?.ReleaseVisualHandlers();
        DetailAttachmentStrip.ReleaseVisualHandlers();
        DetailStepsItemsControl.ItemsSource = null;
    }

    private async Task<TodoItemViewModel?> EnsureDetailItemPersistedAsync()
    {
        if (ViewModel?.SelectedDetailItem is not { } selected)
        {
            return null;
        }

        if (!ViewModel.IsCreatingDetailItem)
        {
            return selected;
        }

        TodoItemViewModel? finalized = await ViewModel.FinalizeDetailAsync(
            DetailTitleTextBox.Text,
            closeDetail: false);
        return finalized;
    }

    private async void DetailAddStepButton_Click(object sender, RoutedEventArgs e) =>
        await AddDetailStepAsync();

    private async void DetailNewStepTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await AddDetailStepAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            DetailNewStepTextBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    private async Task AddDetailStepAsync()
    {
        string text = DetailNewStepTextBox.Text;
        if (string.IsNullOrWhiteSpace(text) || ViewModel is null)
        {
            return;
        }

        TodoItemViewModel? item = await EnsureDetailItemPersistedAsync();
        if (item is null || await ViewModel.AddStepAsync(item.Id, text) is null)
        {
            return;
        }

        DetailNewStepTextBox.Text = string.Empty;
        DetailNewStepTextBox.Focus(FocusState.Programmatic);
    }

    private async void DetailStepCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            await SetDetailStepCompletedAsync(checkBox);
        }
    }

    private async Task<bool> SetDetailStepCompletedAsync(CheckBox checkBox)
    {
        if (checkBox.DataContext is not TodoStepViewModel step ||
            ViewModel?.SelectedDetailItem is not { } item)
        {
            return false;
        }

        return await ViewModel.SetStepCompletedAsync(
            item.Id,
            step.Id,
            checkBox.IsChecked == true);
    }

    private async void DetailStepTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            await SaveDetailStepTextAsync(textBox);
        }
    }

    private async Task<bool> SaveDetailStepTextAsync(TextBox textBox)
    {
        if (textBox.DataContext is not TodoStepViewModel step ||
            ViewModel?.SelectedDetailItem is not { } item)
        {
            return false;
        }

        bool saved = await ViewModel.UpdateStepTextAsync(
            item.Id,
            step.Id,
            textBox.Text);
        if (!saved)
        {
            textBox.Text = step.Text;
        }

        return saved;
    }

    private void DetailStepTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Escape) || sender is not TextBox textBox)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == VirtualKey.Escape && textBox.DataContext is TodoStepViewModel step)
        {
            textBox.Text = step.Text;
        }

        Focus(FocusState.Programmatic);
    }

    private async void DetailDeleteStepButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            await DeleteDetailStepAsync(element);
        }
    }

    private async Task<bool> DeleteDetailStepAsync(FrameworkElement element)
    {
        if (element.DataContext is not TodoStepViewModel step ||
            ViewModel?.SelectedDetailItem is not { } item)
        {
            return false;
        }

        return await ViewModel.DeleteStepAsync(item.Id, step.Id);
    }

    private async void DetailNotesReaderHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel?.SelectedDetailItem is { Notes.Length: 0 })
        {
            await BeginNotesEditingAsync();
            e.Handled = true;
        }
    }

    private async void DetailNotesEditButton_Click(object sender, RoutedEventArgs e) =>
        await BeginNotesEditingAsync();

    private async void DetailNotesDoneButton_Click(object sender, RoutedEventArgs e) =>
        await SaveActiveNotesAsync(keepEditing: false);

    private async void DetailNotesRetryButton_Click(object sender, RoutedEventArgs e) =>
        await SaveActiveNotesAsync(keepEditing: !_notesExitRequested);

    private async void DetailNotesReaderHost_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        await BeginNotesEditingAsync();
        e.Handled = true;
    }

    private MarkdownSourceEditor EnsureDetailNotesEditorRealized()
    {
        if (DetailNotesEditor is null)
        {
            _ = FindName(nameof(DetailNotesEditor));
            if (DetailNotesEditor is not null)
            {
                DetailNotesEditor.TextResolver = App.Current.LocalizationService.T;
            }
        }

        return DetailNotesEditor!;
    }

    private async Task BeginNotesEditingAsync()
    {
        TodoItemViewModel? item = await EnsureDetailItemPersistedAsync();
        if (item is null)
        {
            return;
        }

        MarkdownSourceEditor notesEditor = EnsureDetailNotesEditorRealized();
        _notesEditingItemId = item.Id;
        _notesOriginalText = item.Notes;
        _notesExitRequested = false;
        notesEditor.Text = item.Notes;
        DetailNotesSaveFailure.Visibility = Visibility.Collapsed;
        ApplyNotesEditingVisualState(isEditing: true);
        notesEditor.FocusEditor(moveCaretToEnd: true);
    }

    private void DetailNotesEditor_EditorTextChanged(object? sender, EventArgs e)
    {
        ScheduleNotesAutoSave();
    }

    private void ScheduleNotesAutoSave()
    {
        if (_notesEditingItemId is null)
        {
            return;
        }

        _notesExitRequested = false;
        DetailNotesSaveFailure.Visibility = Visibility.Collapsed;
        _notesAutosaveTimer.Stop();
        _notesAutosaveTimer.Start();
    }

    private async void NotesAutosaveTimer_Tick(object? sender, object e)
    {
        _notesAutosaveTimer.Stop();
        await SaveActiveNotesAsync(keepEditing: true);
    }

    private async void DetailNotesEditor_CommitRequested(object? sender, EventArgs e) =>
        await SaveActiveNotesAsync(keepEditing: false);

    private void DetailNotesEditor_CancelRequested(object? sender, EventArgs e)
    {
        _notesAutosaveTimer.Stop();
        if (DetailNotesEditor is not null)
        {
            DetailNotesEditor.Text = _notesOriginalText;
        }

        EndNotesEditing();
    }

    private async Task<bool> SaveActiveNotesAsync(bool keepEditing)
    {
        _notesAutosaveTimer.Stop();
        if (!keepEditing)
        {
            _notesExitRequested = true;
        }

        if (_notesEditingItemId is not { } itemId || ViewModel is null)
        {
            if (!keepEditing)
            {
                EndNotesEditing();
            }

            return true;
        }

        string source = DetailNotesEditor?.Text ?? _notesOriginalText;
        TodoWidgetViewModel viewModel = ViewModel;
        if (!keepEditing &&
            (_isDisposed || _isFlushingTeardown) &&
            string.Equals(_inFlightNotesSaveItemId, itemId, StringComparison.Ordinal) &&
            string.Equals(_inFlightNotesSaveSource, source, StringComparison.Ordinal))
        {
            _notesEditingItemId = null;
            return true;
        }

        if (_isFlushingTeardown && !keepEditing)
        {
            _notesEditingItemId = null;
        }

        bool saved = false;
        _inFlightNotesSaveItemId = itemId;
        _inFlightNotesSaveSource = source;
        await _notesSaveGate.WaitAsync();
        try
        {
            saved = await viewModel.UpdateNotesAsync(itemId, source);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to save notes for {itemId}: {ex}");
        }
        finally
        {
            if (string.Equals(_inFlightNotesSaveItemId, itemId, StringComparison.Ordinal) &&
                string.Equals(_inFlightNotesSaveSource, source, StringComparison.Ordinal))
            {
                _inFlightNotesSaveItemId = null;
                _inFlightNotesSaveSource = null;
            }

            _notesSaveGate.Release();
        }

        if (_isDisposed || _isFlushingTeardown)
        {
            return saved;
        }

        if (!string.Equals(_notesEditingItemId, itemId, StringComparison.Ordinal))
        {
            return saved;
        }

        if (!saved)
        {
            if (DetailNotesSaveFailure is not null)
            {
                DetailNotesSaveFailure.Visibility = Visibility.Visible;
            }

            ApplyNotesEditingVisualState(isEditing: true);
            return false;
        }

        _notesOriginalText = source;
        if (DetailNotesSaveFailure is not null)
        {
            DetailNotesSaveFailure.Visibility = Visibility.Collapsed;
        }

        bool sourceIsCurrent = DetailNotesEditor is null || string.Equals(
            DetailNotesEditor.Text,
            source,
            StringComparison.Ordinal);
        if (_notesExitRequested && sourceIsCurrent)
        {
            EndNotesEditing();
        }

        return true;
    }

    private void EndNotesEditing()
    {
        _notesAutosaveTimer.Stop();
        _notesEditingItemId = null;
        _notesExitRequested = false;
        if (DetailNotesSaveFailure is not null)
        {
            DetailNotesSaveFailure.Visibility = Visibility.Collapsed;
        }

        ApplyNotesEditingVisualState(isEditing: false);
    }

    private void ApplyNotesEditingVisualState(bool isEditing)
    {
        if (isEditing && !EnsureDetailPageRealized())
        {
            return;
        }

        if (DetailPage is null)
        {
            return;
        }

        DetailNotesReaderHost.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
        if (isEditing)
        {
            EnsureDetailNotesEditorRealized().Visibility = Visibility.Visible;
        }
        else if (DetailNotesEditor is not null)
        {
            DetailNotesEditor.Visibility = Visibility.Collapsed;
        }

        DetailNotesEditButton.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
        DetailNotesDoneButton.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<bool> PrepareForDetailSelectionChangeAsync(string? nextItemId)
    {
        if (_notesEditingItemId is not null &&
            !string.Equals(_notesEditingItemId, nextItemId, StringComparison.Ordinal) &&
            !await SaveActiveNotesAsync(keepEditing: false))
        {
            return false;
        }

        if (ViewModel?.SelectedDetailItem is { } currentItem &&
            !string.Equals(currentItem.Id, nextItemId, StringComparison.Ordinal))
        {
            await FlushDetailTitleAsync(
                finalizeBlankDraft: true,
                closeDetailOnFinalize: false);
        }

        return true;
    }

    private async Task<TodoItemViewModel?> OpenDetailItemAsync(string itemId)
    {
        if (ViewModel is null || !await PrepareForDetailSelectionChangeAsync(itemId))
        {
            return null;
        }

        TodoItemViewModel? opened = ViewModel.OpenDetail(itemId);
        if (opened is not null)
        {
            _ = EnsureDetailPageRealized();
        }

        return opened;
    }

    private void SynchronizeDetailNotes()
    {
        TodoItemViewModel? item = ViewModel?.SelectedDetailItem;
        if (item is not null && string.Equals(_notesEditingItemId, item.Id, StringComparison.Ordinal))
        {
            return;
        }

        if (_notesEditingItemId is not null)
        {
            // Selection/filter changes must not discard a debounce window that
            // has not elapsed yet. The save captures the old item ID and source.
            _ = SaveActiveNotesAsync(keepEditing: false);
        }

        _notesAutosaveTimer.Stop();
        _notesEditingItemId = null;
        _notesExitRequested = false;
        if (item is not null)
        {
            _ = EnsureDetailPageRealized();
        }

        if (DetailPage is null)
        {
            return;
        }

        DetailNotesSaveFailure.Visibility = Visibility.Collapsed;
        ApplyNotesEditingVisualState(isEditing: false);
        DetailNotesView.AttachmentResolver = ResolveSelectedTodoAttachment;
        DetailNotesView.Refresh();
    }

    private async void DetailNotesView_TaskToggleRequested(
        object? sender,
        MarkdownTaskToggleRequestedEventArgs e)
    {
        if (ViewModel?.SelectedDetailItem is not { } item ||
            !_markdownDocumentService.TryToggleTask(item.Notes, e.TaskIndex, out string updated))
        {
            return;
        }

        await ViewModel.UpdateNotesAsync(item.Id, updated);
    }

    private string? ResolveSelectedTodoAttachment(string attachmentId) =>
        ViewModel?.SelectedDetailItem?.Attachments
            .FirstOrDefault(attachment => string.Equals(
                attachment.Id,
                attachmentId,
                StringComparison.Ordinal))
            ?.FilePath;

    private async void DetailNotesView_AttachmentOpenRequested(
        object? sender,
        MarkdownAttachmentRequestedEventArgs e)
    {
        TodoAttachmentViewModel? attachment = ViewModel?.SelectedDetailItem?.Attachments
            .FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                e.AttachmentId,
                StringComparison.Ordinal));
        if (attachment is not null)
        {
            await OpenTodoAttachmentAsync(attachment);
        }
    }
}
