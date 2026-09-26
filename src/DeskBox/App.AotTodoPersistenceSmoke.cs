#if DESKBOX_NATIVE_AOT
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotTodoAutoSaveNotes =
        "AOT Todo real 600 ms auto-save notes";
    private const string AotTodoPersistedTitle =
        "AOT Todo persisted edited title";
    private const string AotTodoExplicitSaveNotes =
        "AOT Todo explicit restart save notes";

    private async Task CaptureAotManagedUiTodoPersistenceAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        if (!DeskBoxDataPathService.Current.IsDevelopmentRoot)
        {
            throw new InvalidOperationException(
                "The Todo persistence matrix requires the isolated preview root.");
        }

        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotTodoPersistenceHost host =
            await manager.GetAotTodoPersistenceHostAsync(AotManagedUiTodoWidgetId);
        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 && host.HasXamlRoot && host.Visible,
            "TodoLiveHost",
            "The owned Todo HWND or XamlRoot is unavailable.");

        TodoWidgetContent surface = host.Surface;
        AotManagedUiTodoPersistenceEvidence evidence =
            result.TodoPersistence ??
            throw new InvalidOperationException(
                "The Todo persistence evidence was not initialized.");

        if (phase == AotManagedUiTodoVerifyDeletePhase)
        {
            TodoWidgetData reloaded =
                await new TodoWidgetStore(AotManagedUiTodoWidgetId).LoadAsync();
            TodoItem item = reloaded.Items.Single(entry => !entry.IsDeleted);
            await surface.OpenAotTodoItemAsync(item.Id);
        }
        evidence.Before = await CaptureAotManagedUiTodoStateAsync(
            surface,
            AotManagedUiTodoWidgetId);

        switch (phase)
        {
            case AotManagedUiTodoMutatePhase:
            {
                RequireAotManagedUiTodoEmpty(evidence.Before);
                AotTodoMutationResult mutation =
                    await surface.RunAotTodoMutationAsync(AotTodoAutoSaveNotes);
                evidence.AutoSaveObserved = mutation.AutoSaveObserved;
                evidence.After = await CaptureAotManagedUiTodoStateAsync(
                    surface,
                    AotManagedUiTodoWidgetId);
                RequireAotManagedUiTodoPopulated(
                    evidence.After,
                    mutation.PersistedTitle,
                    AotTodoAutoSaveNotes,
                    expectedCompleted: true);
                RequireAotManagedUi(
                    result,
                    mutation.AutoSaveObserved &&
                    string.Equals(
                        evidence.After.Items.Single().Id,
                        mutation.ItemId,
                        StringComparison.Ordinal),
                    "TodoTaskTitleNotesAndCompletionPersisted",
                    "The Todo task, edited title, auto-saved notes, or completion did not persist.");
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoNotesAutoSaveObserved",
                    "The real Todo notes auto-save was not observed.");
                break;
            }

            case AotManagedUiTodoVerifyDeletePhase:
            {
                RequireAotManagedUiTodoPopulated(
                    evidence.Before,
                    AotTodoPersistedTitle,
                    AotTodoAutoSaveNotes,
                    expectedCompleted: true);
                string itemId = evidence.Before.Items.Single().Id;
                AotTodoExplicitSaveResult explicitSave =
                    await surface.ApplyAotTodoExplicitRestartEditsAsync(
                        itemId,
                        AotTodoExplicitSaveNotes);
                evidence.ExplicitNotesSaved = explicitSave.ExplicitNotesSaved;
                evidence.CompletionRoundTripObserved =
                    explicitSave.CompletionRoundTripObserved;
                evidence.AfterExplicitSave =
                    await CaptureAotManagedUiTodoStateAsync(
                        surface,
                        AotManagedUiTodoWidgetId);
                RequireAotManagedUiTodoPopulated(
                    evidence.AfterExplicitSave,
                    AotTodoPersistedTitle,
                    AotTodoExplicitSaveNotes,
                    expectedCompleted: false);
                RequireAotManagedUi(
                    result,
                    explicitSave.ExplicitNotesSaved &&
                    explicitSave.CompletionRoundTripObserved,
                    "TodoRestartExplicitSaveAndCompletionVerified",
                    "The reloaded Todo task did not complete explicit notes save and completion round-trip.");

                await surface.DeleteAotTodoItemAsync(itemId);
                evidence.After = await CaptureAotManagedUiTodoStateAsync(
                    surface,
                    AotManagedUiTodoWidgetId);
                RequireAotManagedUiTodoEmpty(evidence.After);
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoItemDeleted",
                    "The product Todo item deletion path did not complete.");
                break;
            }

            case AotManagedUiTodoPostflightPhase:
                RequireAotManagedUiTodoEmpty(evidence.Before);
                evidence.After = await CaptureAotManagedUiTodoStateAsync(
                    surface,
                    AotManagedUiTodoWidgetId);
                RequireAotManagedUiTodoEmpty(evidence.After);
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoDeletePostflightVerified",
                    "The Todo delete postflight was not clean.");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Todo persistence phase '{phase}'.");
        }
    }

    private async Task<AotManagedUiTodoStateEvidence>
        CaptureAotManagedUiTodoStateAsync(
            TodoWidgetContent surface,
            string widgetId)
    {
        var store = new TodoWidgetStore(widgetId);
        TodoWidgetData data = await store.LoadAsync();
        AotTodoSurfaceSnapshot surfaceSnapshot =
            surface.CaptureAotTodoSurfaceSnapshot();
        string[] managedAttachmentRelativePaths =
            Directory.Exists(store.AttachmentDirectory)
                ? Directory.GetFiles(
                        store.AttachmentDirectory,
                        "*",
                        SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(
                            store.AttachmentDirectory,
                            path)
                        .Replace('\\', '/'))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray()
                : [];

        return new AotManagedUiTodoStateEvidence
        {
            StoreVersion = data.Version,
            StoreFileExists = File.Exists(store.StorePath),
            // Deletes persist as soft-delete tombstones (merge safety), so the
            // matrix projects only live items — the same contract as the
            // Quick Capture persistence evidence.
            Items = data.Items
                .Where(item => !item.IsDeleted)
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new AotManagedUiTodoItemEvidence
                {
                    Id = item.Id,
                    Text = item.Text,
                    Notes = item.Notes ?? string.Empty,
                    IsCompleted = item.IsCompleted,
                    HasCompletedAt = item.CompletedAt is not null,
                    IsImportant = item.IsImportant,
                    HasDueDate = item.DueDate is not null,
                    HasRecurrence = item.Recurrence is not null,
                    StepCount = item.Steps.Count,
                    Steps = item.Steps
                        .OrderBy(step => step.SortOrder)
                        .Select(step => new AotManagedUiTodoStepEvidence
                        {
                            Id = step.Id,
                            Text = step.Text,
                            IsCompleted = step.IsCompleted,
                            SortOrder = step.SortOrder
                        })
                        .ToList(),
                    AttachmentCount = item.Attachments.Count,
                    Attachments = item.Attachments
                        .OrderBy(attachment => attachment.Id, StringComparer.Ordinal)
                        .Select(attachment => new AotManagedUiTodoAttachmentEvidence
                        {
                            Id = attachment.Id,
                            FilePath = attachment.FilePath,
                            DisplayName = attachment.DisplayName,
                            Type = attachment.Type,
                            StorageMode = attachment.StorageMode,
                            IsManagedCopy = attachment.IsManagedCopy,
                            Exists = File.Exists(attachment.FilePath),
                            FileLength = File.Exists(attachment.FilePath)
                                ? new FileInfo(attachment.FilePath).Length
                                : null,
                            AddedAt = attachment.AddedAt
                        })
                        .ToList(),
                    ReminderOffsetMinutes = item.ReminderOffsetMinutes,
                    SortOrder = item.SortOrder,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt
                })
                .ToList(),
            ManagedAttachmentDirectoryExists =
                Directory.Exists(store.AttachmentDirectory),
            ManagedAttachmentFileCount = managedAttachmentRelativePaths.Length,
            ManagedAttachmentRelativePaths =
                managedAttachmentRelativePaths.ToList(),
            SurfaceInitialized = surfaceSnapshot.IsInitialized,
            SurfaceLoaded = surfaceSnapshot.IsLoaded,
            SurfaceHasXamlRoot = surfaceSnapshot.HasXamlRoot,
            SurfaceItemCount = surfaceSnapshot.SurfaceItemCount,
            VisibleItemCount = surfaceSnapshot.VisibleItemCount,
            DetailItemId = surfaceSnapshot.DetailItemId,
            DetailTitle = surfaceSnapshot.DetailTitle,
            DetailNotes = surfaceSnapshot.DetailNotes,
            DetailIsCreating = surfaceSnapshot.IsCreatingDetail,
            NotesEditingItemId = surfaceSnapshot.NotesEditingItemId,
            NotesAutoSavePending = surfaceSnapshot.NotesAutoSavePending,
            NotesSaveGateCount = surfaceSnapshot.NotesSaveGateCount,
            StepUiItemCount = surfaceSnapshot.StepUiItemCount,
            StepUiContainerRealized = surfaceSnapshot.StepUiContainerRealized,
            StepUiDataContextId = surfaceSnapshot.StepUiDataContextId,
            StepUiText = surfaceSnapshot.StepUiText,
            StepUiIsChecked = surfaceSnapshot.StepUiIsChecked,
            StepUiOpacity = surfaceSnapshot.StepUiOpacity,
            AttachmentUiItemCount = surfaceSnapshot.AttachmentUiItemCount,
            AttachmentUiContainerRealized =
                surfaceSnapshot.AttachmentUiContainerRealized,
            AttachmentUiDataContextId =
                surfaceSnapshot.AttachmentUiDataContextId,
            AttachmentUiDisplayName = surfaceSnapshot.AttachmentUiDisplayName,
            AttachmentUiType = surfaceSnapshot.AttachmentUiType,
            AttachmentUiStorageMode = surfaceSnapshot.AttachmentUiStorageMode,
            AttachmentUiExists = surfaceSnapshot.AttachmentUiExists,
            AttachmentUiDisplayNameProjected =
                surfaceSnapshot.AttachmentUiDisplayNameProjected,
            AttachmentUiGlyph = surfaceSnapshot.AttachmentUiGlyph,
            AttachmentUiGlyphProjected =
                surfaceSnapshot.AttachmentUiGlyphProjected,
            AttachmentUiRemoveButtonFound =
                surfaceSnapshot.AttachmentUiRemoveButtonFound,
            AttachmentUiOpenAutomationName =
                surfaceSnapshot.AttachmentUiOpenAutomationName
        };
    }

    private static void RequireAotManagedUiTodoEmpty(
        AotManagedUiTodoStateEvidence state)
    {
        if (state.StoreVersion != 3 ||
            state.Items.Count != 0 ||
            !state.SurfaceInitialized ||
            !state.SurfaceLoaded ||
            !state.SurfaceHasXamlRoot ||
            state.SurfaceItemCount != 0 ||
            state.VisibleItemCount != 0 ||
            state.DetailItemId is not null ||
            state.DetailIsCreating ||
            state.NotesEditingItemId is not null ||
            state.NotesAutoSavePending ||
            state.NotesSaveGateCount != 1 ||
            state.StepUiItemCount != 0 ||
            state.StepUiContainerRealized ||
            state.StepUiDataContextId is not null ||
            state.StepUiText.Length != 0 ||
            state.StepUiIsChecked is not null ||
            state.StepUiOpacity is not null ||
            state.ManagedAttachmentFileCount != 0 ||
            state.ManagedAttachmentRelativePaths.Count != 0 ||
            state.AttachmentUiItemCount != 0 ||
            state.AttachmentUiContainerRealized ||
            state.AttachmentUiDataContextId is not null ||
            state.AttachmentUiDisplayName.Length != 0 ||
            state.AttachmentUiType.Length != 0 ||
            state.AttachmentUiStorageMode.Length != 0 ||
            state.AttachmentUiExists ||
            state.AttachmentUiDisplayNameProjected ||
            state.AttachmentUiGlyph.Length != 0 ||
            state.AttachmentUiGlyphProjected ||
            state.AttachmentUiRemoveButtonFound ||
            state.AttachmentUiOpenAutomationName.Length != 0)
        {
            throw new InvalidOperationException(
                "The Todo store or real surface baseline is not empty.");
        }
    }

    private static void RequireAotManagedUiTodoPopulated(
        AotManagedUiTodoStateEvidence state,
        string expectedTitle,
        string expectedNotes,
        bool expectedCompleted)
    {
        AotManagedUiTodoItemEvidence item = state.Items.Single();
        if (state.StoreVersion != 3 ||
            !state.StoreFileExists ||
            !string.Equals(item.Text, expectedTitle, StringComparison.Ordinal) ||
            !string.Equals(item.Notes, expectedNotes, StringComparison.Ordinal) ||
            item.IsCompleted != expectedCompleted ||
            item.HasCompletedAt != expectedCompleted ||
            item.IsImportant ||
            item.HasDueDate ||
            item.HasRecurrence ||
            item.StepCount != 0 ||
            item.Steps.Count != 0 ||
            item.AttachmentCount != 0 ||
            item.ReminderOffsetMinutes is not null ||
            item.SortOrder != 0 ||
            !state.SurfaceInitialized ||
            !state.SurfaceLoaded ||
            !state.SurfaceHasXamlRoot ||
            state.SurfaceItemCount != 1 ||
            state.VisibleItemCount != 1 ||
            !string.Equals(state.DetailItemId, item.Id, StringComparison.Ordinal) ||
            !string.Equals(state.DetailTitle, expectedTitle, StringComparison.Ordinal) ||
            !string.Equals(state.DetailNotes, expectedNotes, StringComparison.Ordinal) ||
            state.DetailIsCreating ||
            state.NotesEditingItemId is not null ||
            state.NotesAutoSavePending ||
            state.NotesSaveGateCount != 1 ||
            state.StepUiItemCount != 0 ||
            state.StepUiContainerRealized ||
            state.StepUiDataContextId is not null ||
            state.StepUiText.Length != 0 ||
            state.StepUiIsChecked is not null ||
            state.StepUiOpacity is not null ||
            state.ManagedAttachmentFileCount != 0 ||
            state.ManagedAttachmentRelativePaths.Count != 0 ||
            state.AttachmentUiItemCount != 0 ||
            state.AttachmentUiContainerRealized ||
            state.AttachmentUiDataContextId is not null ||
            state.AttachmentUiDisplayName.Length != 0 ||
            state.AttachmentUiType.Length != 0 ||
            state.AttachmentUiStorageMode.Length != 0 ||
            state.AttachmentUiExists ||
            state.AttachmentUiDisplayNameProjected ||
            state.AttachmentUiGlyph.Length != 0 ||
            state.AttachmentUiGlyphProjected ||
            state.AttachmentUiRemoveButtonFound ||
            state.AttachmentUiOpenAutomationName.Length != 0)
        {
            throw new InvalidOperationException(
                "The Todo store, core task, notes, completion, or real detail state is incomplete.");
        }
    }
}

internal sealed class AotManagedUiTodoPersistenceEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool AutoSaveObserved { get; set; }
    public bool ExplicitNotesSaved { get; set; }
    public bool CompletionRoundTripObserved { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public AotManagedUiTodoStateEvidence Before { get; set; } = new();
    public AotManagedUiTodoStateEvidence? AfterExplicitSave { get; set; }
    public AotManagedUiTodoStateEvidence After { get; set; } = new();
}

internal sealed class AotManagedUiTodoStateEvidence
{
    public int StoreVersion { get; set; }
    public bool StoreFileExists { get; set; }
    public List<AotManagedUiTodoItemEvidence> Items { get; set; } = [];
    public bool ManagedAttachmentDirectoryExists { get; set; }
    public int ManagedAttachmentFileCount { get; set; }
    public List<string> ManagedAttachmentRelativePaths { get; set; } = [];
    public bool SurfaceInitialized { get; set; }
    public bool SurfaceLoaded { get; set; }
    public bool SurfaceHasXamlRoot { get; set; }
    public int SurfaceItemCount { get; set; }
    public int VisibleItemCount { get; set; }
    public string? DetailItemId { get; set; }
    public string DetailTitle { get; set; } = string.Empty;
    public string DetailNotes { get; set; } = string.Empty;
    public bool DetailIsCreating { get; set; }
    public string? NotesEditingItemId { get; set; }
    public bool NotesAutoSavePending { get; set; }
    public int NotesSaveGateCount { get; set; }
    public int StepUiItemCount { get; set; }
    public bool StepUiContainerRealized { get; set; }
    public string? StepUiDataContextId { get; set; }
    public string StepUiText { get; set; } = string.Empty;
    public bool? StepUiIsChecked { get; set; }
    public double? StepUiOpacity { get; set; }
    public int AttachmentUiItemCount { get; set; }
    public bool AttachmentUiContainerRealized { get; set; }
    public string? AttachmentUiDataContextId { get; set; }
    public string AttachmentUiDisplayName { get; set; } = string.Empty;
    public string AttachmentUiType { get; set; } = string.Empty;
    public string AttachmentUiStorageMode { get; set; } = string.Empty;
    public bool AttachmentUiExists { get; set; }
    public bool AttachmentUiDisplayNameProjected { get; set; }
    public string AttachmentUiGlyph { get; set; } = string.Empty;
    public bool AttachmentUiGlyphProjected { get; set; }
    public bool AttachmentUiRemoveButtonFound { get; set; }
    public string AttachmentUiOpenAutomationName { get; set; } = string.Empty;
}

internal sealed class AotManagedUiTodoItemEvidence
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public bool HasCompletedAt { get; set; }
    public bool IsImportant { get; set; }
    public bool HasDueDate { get; set; }
    public bool HasRecurrence { get; set; }
    public int StepCount { get; set; }
    public List<AotManagedUiTodoStepEvidence> Steps { get; set; } = [];
    public int AttachmentCount { get; set; }
    public List<AotManagedUiTodoAttachmentEvidence> Attachments { get; set; } = [];
    public int? ReminderOffsetMinutes { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class AotManagedUiTodoStepEvidence
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int SortOrder { get; set; }
}

internal sealed class AotManagedUiTodoAttachmentEvidence
{
    public string Id { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string StorageMode { get; set; } = string.Empty;
    public bool IsManagedCopy { get; set; }
    public bool Exists { get; set; }
    public long? FileLength { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}
#endif
