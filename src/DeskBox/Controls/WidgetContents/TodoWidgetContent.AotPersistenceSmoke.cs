#if DESKBOX_NATIVE_AOT
using DeskBox.Controls;
using DeskBox.ViewModels;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private const string AotTodoInitialTitle = "AOT Todo initial task";
    private const string AotTodoPersistedTitle = "AOT Todo persisted edited title";

    internal AotTodoSurfaceSnapshot CaptureAotTodoSurfaceSnapshot()
    {
        TodoItemViewModel? detailItem = ViewModel?.SelectedDetailItem;
        AotTodoStepUiSnapshot stepUi = CaptureAotTodoStepUiSnapshot();
        AotAttachmentTileSnapshot attachmentUi =
            DetailAttachmentStrip is not null
                ? DetailAttachmentStrip.CaptureAotAttachmentTileSnapshot()
                : new AotAttachmentTileSnapshot(
                    ItemCount: 0,
                    ContainerRealized: false,
                    DataContextId: null,
                    DisplayName: string.Empty,
                    Type: string.Empty,
                    StorageMode: string.Empty,
                    Exists: false,
                    DisplayNameProjected: false,
                    Glyph: string.Empty,
                    GlyphProjected: false,
                    RemoveButtonFound: false,
                    OpenAutomationName: string.Empty);
        return new AotTodoSurfaceSnapshot(
            ViewModel?.IsInitialized == true,
            IsLoaded,
            XamlRoot is not null,
            ViewModel?.Items.Count ?? 0,
            ViewModel?.VisibleItems.Count ?? 0,
            detailItem?.Id,
            detailItem?.Text ?? string.Empty,
            detailItem?.Notes ?? string.Empty,
            ViewModel?.IsCreatingDetailItem == true,
            _notesEditingItemId,
            _notesAutosaveTimer.IsEnabled,
            _notesSaveGate.CurrentCount,
            stepUi.ItemCount,
            stepUi.ContainerRealized,
            stepUi.DataContextId,
            stepUi.Text,
            stepUi.IsChecked,
            stepUi.Opacity,
            attachmentUi.ItemCount,
            attachmentUi.ContainerRealized,
            attachmentUi.DataContextId,
            attachmentUi.DisplayName,
            attachmentUi.Type,
            attachmentUi.StorageMode,
            attachmentUi.Exists,
            attachmentUi.DisplayNameProjected,
            attachmentUi.Glyph,
            attachmentUi.GlyphProjected,
            attachmentUi.RemoveButtonFound,
            attachmentUi.OpenAutomationName);
    }

    internal async Task<AotTodoMutationResult> RunAotTodoMutationAsync(
        string autoSaveNotes)
    {
        if (ViewModel is null || !ViewModel.IsInitialized)
        {
            throw new InvalidOperationException(
                "The Todo mutation surface is not initialized.");
        }
        if (ViewModel.Items.Count != 0)
        {
            throw new InvalidOperationException(
                "The Todo mutation phase did not start from an empty store.");
        }

        await OpenAddEditorAsync();
        if (!ViewModel.IsCreatingDetailItem || ViewModel.SelectedDetailItem is null)
        {
            throw new InvalidOperationException(
                "The real Todo add-detail path did not create a draft.");
        }

        DetailTitleTextBox.Text = AotTodoInitialTitle;
        TodoItemViewModel item = await ViewModel.FinalizeDetailAsync(
            DetailTitleTextBox.Text,
            closeDetail: false) ??
            throw new InvalidOperationException(
                "The real Todo detail path did not persist the initial task.");

        DetailTitleTextBox.Text = AotTodoPersistedTitle;
        if (!await SaveDetailEditorsAsync(item) ||
            !string.Equals(item.Text, AotTodoPersistedTitle, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The real Todo detail save path did not persist the edited title.");
        }

        await BeginNotesEditingAsync();
        _ = EnsureDetailNotesEditorRealized();
        if (DetailNotesEditor is not null)
        {
            DetailNotesEditor.Text = autoSaveNotes;
        }
        // The public editor property is the control's data contract. Pair it
        // with the same scheduling entry point as the real EditorTextChanged
        // handler, then let the real 600 ms DispatcherTimer and product save
        // path complete the operation.
        ScheduleNotesAutoSave();
        bool autoSaveObserved = await WaitForAotTodoAutoSaveAsync(
            item,
            autoSaveNotes);
        if (!autoSaveObserved)
        {
            throw new InvalidOperationException(
                "The real Todo 600 ms notes auto-save did not complete. " +
                $"TimerEnabled={_notesAutosaveTimer.IsEnabled}; " +
                $"SaveGateCount={_notesSaveGate.CurrentCount}; " +
                $"EditingItemId={_notesEditingItemId ?? "<null>"}; " +
                $"EditorMatches={string.Equals(DetailNotesEditor?.Text, autoSaveNotes, StringComparison.Ordinal)}; " +
                $"OriginalMatches={string.Equals(_notesOriginalText, autoSaveNotes, StringComparison.Ordinal)}; " +
                $"ItemMatches={string.Equals(item.Notes, autoSaveNotes, StringComparison.Ordinal)}.");
        }

        // End the editor only after the timer has persisted the revision so the
        // next process observes the same stable detail state.
        if (!await SaveActiveNotesAsync(keepEditing: false))
        {
            throw new InvalidOperationException(
                "The Todo notes editor could not leave its saved state.");
        }

        if (!await SetCompletedWithFeedbackAsync(item, isCompleted: true) ||
            !item.IsCompleted ||
            item.CompletedAt is null)
        {
            throw new InvalidOperationException(
                "The product Todo completion path did not persist completion.");
        }

        return new AotTodoMutationResult(
            item.Id,
            autoSaveObserved,
            AotTodoPersistedTitle);
    }

    internal async Task<AotTodoExplicitSaveResult>
        ApplyAotTodoExplicitRestartEditsAsync(
            string itemId,
            string explicitNotes)
    {
        TodoItemViewModel item = await OpenAotTodoItemAsync(itemId);
        bool wasCompleted = item.IsCompleted && item.CompletedAt is not null;

        await BeginNotesEditingAsync();
        EnsureDetailNotesEditorRealized().Text = explicitNotes;
        bool explicitNotesSaved =
            await SaveActiveNotesAsync(keepEditing: false) &&
            string.Equals(item.Notes, explicitNotes, StringComparison.Ordinal) &&
            string.Equals(DetailNotesEditor?.Text, explicitNotes, StringComparison.Ordinal) &&
            !_notesAutosaveTimer.IsEnabled &&
            _notesEditingItemId is null &&
            _notesSaveGate.CurrentCount == 1;
        if (!explicitNotesSaved)
        {
            throw new InvalidOperationException(
                "The real Todo explicit notes save did not complete.");
        }

        bool completionUpdated =
            await SetCompletedWithFeedbackAsync(item, isCompleted: false);
        bool completionRoundTripObserved =
            wasCompleted &&
            completionUpdated &&
            !item.IsCompleted &&
            item.CompletedAt is null;
        if (!completionRoundTripObserved)
        {
            throw new InvalidOperationException(
                "The Todo completion state did not round-trip after restart.");
        }

        return new AotTodoExplicitSaveResult(
            item.Id,
            explicitNotesSaved,
            completionRoundTripObserved);
    }

    internal async Task DeleteAotTodoItemAsync(string itemId)
    {
        TodoItemViewModel item = ViewModel?.Items.Single(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The owned Todo item is unavailable for deletion.");

        // The explicit notes save has already completed. Closing the detail
        // keeps this persistence probe independent of animation timing while
        // still using the product surface deletion path.
        ViewModel.CloseDetail();
        await DeleteItemAsync(item);
        if (ViewModel.Items.Any(candidate =>
                string.Equals(candidate.Id, itemId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The product Todo delete path left the task visible.");
        }
    }

    internal async Task<TodoItemViewModel> OpenAotTodoItemAsync(string itemId)
    {
        TodoWidgetViewModel viewModel = ViewModel ??
            throw new InvalidOperationException(
                "The owned Todo view model is unavailable.");
        TodoItemViewModel item = await OpenDetailItemAsync(itemId) ??
            throw new InvalidOperationException(
                "The owned Todo detail could not be opened.");
        await Task.Yield();
        if (!string.Equals(
                viewModel.SelectedDetailItem?.Id,
                itemId,
                StringComparison.Ordinal) ||
            viewModel.IsCreatingDetailItem ||
            !string.Equals(DetailTitleTextBox.Text, item.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The real Todo detail UI did not select the persisted task.");
        }

        return item;
    }

    private async Task<bool> WaitForAotTodoAutoSaveAsync(
        TodoItemViewModel item,
        string expectedNotes)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!_notesAutosaveTimer.IsEnabled &&
                _notesSaveGate.CurrentCount == 1 &&
                string.Equals(_notesEditingItemId, item.Id, StringComparison.Ordinal) &&
                string.Equals(_notesOriginalText, expectedNotes, StringComparison.Ordinal) &&
                string.Equals(DetailNotesEditor?.Text, expectedNotes, StringComparison.Ordinal) &&
                string.Equals(item.Notes, expectedNotes, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }
}

internal sealed record AotTodoSurfaceSnapshot(
    bool IsInitialized,
    bool IsLoaded,
    bool HasXamlRoot,
    int SurfaceItemCount,
    int VisibleItemCount,
    string? DetailItemId,
    string DetailTitle,
    string DetailNotes,
    bool IsCreatingDetail,
    string? NotesEditingItemId,
    bool NotesAutoSavePending,
    int NotesSaveGateCount,
    int StepUiItemCount,
    bool StepUiContainerRealized,
    string? StepUiDataContextId,
    string StepUiText,
    bool? StepUiIsChecked,
    double? StepUiOpacity,
    int AttachmentUiItemCount,
    bool AttachmentUiContainerRealized,
    string? AttachmentUiDataContextId,
    string AttachmentUiDisplayName,
    string AttachmentUiType,
    string AttachmentUiStorageMode,
    bool AttachmentUiExists,
    bool AttachmentUiDisplayNameProjected,
    string AttachmentUiGlyph,
    bool AttachmentUiGlyphProjected,
    bool AttachmentUiRemoveButtonFound,
    string AttachmentUiOpenAutomationName);

internal sealed record AotTodoMutationResult(
    string ItemId,
    bool AutoSaveObserved,
    string PersistedTitle);

internal sealed record AotTodoExplicitSaveResult(
    string ItemId,
    bool ExplicitNotesSaved,
    bool CompletionRoundTripObserved);
#endif
