#if DESKBOX_NATIVE_AOT
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotTodoAttachmentsExpectedTaskTitle =
        "AOT Todo managed attachment task";
    private const string AotTodoAttachmentsExpectedDisplayName =
        "todo-managed-attachment.txt";

    private async Task CaptureAotManagedUiTodoAttachmentsPersistenceAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        if (!DeskBoxDataPathService.Current.IsDevelopmentRoot)
        {
            throw new InvalidOperationException(
                "The Todo attachments persistence matrix requires the isolated preview root.");
        }

        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotTodoPersistenceHost host =
            await manager.GetAotTodoPersistenceHostAsync(
                AotManagedUiTodoAttachmentsWidgetId);
        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 && host.HasXamlRoot && host.Visible,
            "TodoAttachmentsLiveHost",
            "The owned Todo attachments HWND or XamlRoot is unavailable.");

        TodoWidgetContent surface = host.Surface;
        AotManagedUiTodoAttachmentsPersistenceEvidence evidence =
            result.TodoAttachmentsPersistence ??
            throw new InvalidOperationException(
                "The Todo attachments persistence evidence was not initialized.");

        if (phase == AotManagedUiTodoAttachmentsVerifyDeletePhase)
        {
            TodoWidgetData reloaded = await new TodoWidgetStore(
                AotManagedUiTodoAttachmentsWidgetId).LoadAsync();
            TodoItem item = reloaded.Items.Single(entry => !entry.IsDeleted);
            TodoAttachment attachment = item.Attachments.Single();
            evidence.RestartAttachmentUiProjected =
                await surface.WaitForAotTodoAttachmentProjectionAsync(
                    item.Id,
                    attachment.Id);
        }
        evidence.Before = await CaptureAotManagedUiTodoStateAsync(
            surface,
            AotManagedUiTodoAttachmentsWidgetId);

        switch (phase)
        {
            case AotManagedUiTodoAttachmentsMutatePhase:
            {
                RequireAotManagedUiTodoEmpty(evidence.Before);
                string fixturePath = Path.Combine(
                    DeskBoxDataPathService.Current.RootPath,
                    "fixtures",
                    AotTodoAttachmentsExpectedDisplayName);
                AotTodoAttachmentMutationResult mutation =
                    await surface.RunAotTodoAttachmentMutationAsync(fixturePath);
                evidence.InitialAttachmentUiProjected =
                    mutation.InitialAttachmentUiProjected;
                evidence.ManagedAttachmentPath = mutation.ManagedAttachmentPath;
                evidence.After = await CaptureAotManagedUiTodoStateAsync(
                    surface,
                    AotManagedUiTodoAttachmentsWidgetId);
                RequireAotManagedUiTodoAttachmentPopulated(
                    evidence.After,
                    mutation.ItemId,
                    mutation.AttachmentId);
                RequireAotManagedUi(
                    result,
                    mutation.InitialAttachmentUiProjected,
                    "TodoManagedAttachmentUiProjected",
                    "The imported Todo attachment did not project through the real tile UI.");
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoManagedAttachmentPersisted",
                    "The managed Todo attachment metadata or physical file did not persist.");
                break;
            }

            case AotManagedUiTodoAttachmentsVerifyDeletePhase:
            {
                AotManagedUiTodoItemEvidence beforeItem =
                    RequireAotManagedUiTodoAttachmentPopulated(evidence.Before);
                AotManagedUiTodoAttachmentEvidence beforeAttachment =
                    beforeItem.Attachments.Single();
                evidence.ManagedAttachmentPath = beforeAttachment.FilePath;
                RequireAotManagedUi(
                    result,
                    evidence.RestartAttachmentUiProjected,
                    "TodoManagedAttachmentRestartProjectionVerified",
                    "The persisted Todo attachment did not reload through the real tile UI.");

                AotTodoAttachmentDeleteResult deletion =
                    await surface.DeleteAotTodoManagedAttachmentAsync(
                        beforeItem.Id,
                        beforeAttachment.Id);
                evidence.ManagedAttachmentDeleted =
                    deletion.ManagedAttachmentDeleted;
                evidence.AfterAttachmentDelete =
                    await CaptureAotManagedUiTodoStateAsync(
                        surface,
                        AotManagedUiTodoAttachmentsWidgetId);
                RequireAotManagedUiTodoTaskWithoutAttachments(
                    evidence.AfterAttachmentDelete,
                    deletion.ItemId);
                RequireAotManagedUi(
                    result,
                    deletion.ManagedAttachmentDeleted &&
                    !File.Exists(deletion.ManagedAttachmentPath),
                    "TodoManagedAttachmentDeleted",
                    "The product attachment delete path left metadata, UI, or its managed file behind.");

                await surface.DeleteAotTodoItemAsync(deletion.ItemId);
                evidence.After = await CaptureAotManagedUiTodoStateAsync(
                    surface,
                    AotManagedUiTodoAttachmentsWidgetId);
                RequireAotManagedUiTodoEmpty(evidence.After);
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoAttachmentsItemDeleted",
                    "The Todo attachment fixture task was not deleted.");
                break;
            }

            case AotManagedUiTodoAttachmentsPostflightPhase:
                RequireAotManagedUiTodoEmpty(evidence.Before);
                evidence.After = await CaptureAotManagedUiTodoStateAsync(
                    surface,
                    AotManagedUiTodoAttachmentsWidgetId);
                RequireAotManagedUiTodoEmpty(evidence.After);
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoAttachmentsDeletePostflightVerified",
                    "The Todo attachments delete postflight was not clean.");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Todo attachments persistence phase '{phase}'.");
        }
    }

    private static AotManagedUiTodoItemEvidence
        RequireAotManagedUiTodoAttachmentPopulated(
            AotManagedUiTodoStateEvidence state)
    {
        AotManagedUiTodoItemEvidence item = state.Items.Single();
        AotManagedUiTodoAttachmentEvidence attachment =
            item.Attachments.Single();
        RequireAotManagedUiTodoAttachmentPopulated(
            state,
            item.Id,
            attachment.Id);
        return item;
    }

    private static void RequireAotManagedUiTodoAttachmentPopulated(
        AotManagedUiTodoStateEvidence state,
        string expectedItemId,
        string expectedAttachmentId)
    {
        AotManagedUiTodoItemEvidence item = state.Items.Single();
        AotManagedUiTodoAttachmentEvidence attachment =
            item.Attachments.Single();
        string expectedRelativeSuffix =
            $"{item.Id}/{AotTodoAttachmentsExpectedDisplayName}";
        if (state.StoreVersion != 3 ||
            !state.StoreFileExists ||
            !string.Equals(item.Id, expectedItemId, StringComparison.Ordinal) ||
            !string.Equals(
                item.Text,
                AotTodoAttachmentsExpectedTaskTitle,
                StringComparison.Ordinal) ||
            item.Notes.Length != 0 ||
            item.IsCompleted ||
            item.HasCompletedAt ||
            item.IsImportant ||
            item.HasDueDate ||
            item.HasRecurrence ||
            item.StepCount != 0 ||
            item.Steps.Count != 0 ||
            item.AttachmentCount != 1 ||
            item.Attachments.Count != 1 ||
            item.ReminderOffsetMinutes is not null ||
            item.SortOrder != 0 ||
            !string.Equals(
                attachment.Id,
                expectedAttachmentId,
                StringComparison.Ordinal) ||
            !string.Equals(
                attachment.DisplayName,
                AotTodoAttachmentsExpectedDisplayName,
                StringComparison.Ordinal) ||
            !string.Equals(attachment.Type, "file", StringComparison.Ordinal) ||
            !string.Equals(
                attachment.StorageMode,
                TodoAttachment.ManagedStorageMode,
                StringComparison.Ordinal) ||
            !attachment.IsManagedCopy ||
            !attachment.Exists ||
            attachment.FileLength is not > 0 ||
            !state.ManagedAttachmentDirectoryExists ||
            state.ManagedAttachmentFileCount != 1 ||
            state.ManagedAttachmentRelativePaths.Count != 1 ||
            !state.ManagedAttachmentRelativePaths.Single().EndsWith(
                expectedRelativeSuffix,
                StringComparison.Ordinal) ||
            !state.SurfaceInitialized ||
            !state.SurfaceLoaded ||
            !state.SurfaceHasXamlRoot ||
            state.SurfaceItemCount != 1 ||
            state.VisibleItemCount != 1 ||
            !string.Equals(state.DetailItemId, item.Id, StringComparison.Ordinal) ||
            !string.Equals(state.DetailTitle, item.Text, StringComparison.Ordinal) ||
            state.DetailNotes.Length != 0 ||
            state.DetailIsCreating ||
            state.NotesEditingItemId is not null ||
            state.NotesAutoSavePending ||
            state.NotesSaveGateCount != 1 ||
            state.StepUiItemCount != 0 ||
            state.StepUiContainerRealized ||
            state.AttachmentUiItemCount != 1 ||
            !state.AttachmentUiContainerRealized ||
            !string.Equals(
                state.AttachmentUiDataContextId,
                attachment.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                state.AttachmentUiDisplayName,
                attachment.DisplayName,
                StringComparison.Ordinal) ||
            !string.Equals(
                state.AttachmentUiType,
                attachment.Type,
                StringComparison.Ordinal) ||
            !string.Equals(
                state.AttachmentUiStorageMode,
                attachment.StorageMode,
                StringComparison.Ordinal) ||
            !state.AttachmentUiExists ||
            !state.AttachmentUiDisplayNameProjected ||
            !string.Equals(
                state.AttachmentUiGlyph,
                "\uE8A5",
                StringComparison.Ordinal) ||
            !state.AttachmentUiGlyphProjected ||
            !state.AttachmentUiRemoveButtonFound ||
            !string.Equals(
                state.AttachmentUiOpenAutomationName,
                attachment.DisplayName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Todo managed attachment store, file, ViewModel, or real tile projection is incomplete.");
        }
    }

    private static void RequireAotManagedUiTodoTaskWithoutAttachments(
        AotManagedUiTodoStateEvidence state,
        string expectedItemId)
    {
        AotManagedUiTodoItemEvidence item = state.Items.Single();
        if (state.StoreVersion != 3 ||
            !state.StoreFileExists ||
            !string.Equals(item.Id, expectedItemId, StringComparison.Ordinal) ||
            !string.Equals(
                item.Text,
                AotTodoAttachmentsExpectedTaskTitle,
                StringComparison.Ordinal) ||
            item.StepCount != 0 ||
            item.Steps.Count != 0 ||
            item.AttachmentCount != 0 ||
            item.Attachments.Count != 0 ||
            state.ManagedAttachmentFileCount != 0 ||
            state.ManagedAttachmentRelativePaths.Count != 0 ||
            state.SurfaceItemCount != 1 ||
            state.VisibleItemCount != 1 ||
            !string.Equals(state.DetailItemId, item.Id, StringComparison.Ordinal) ||
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
                "The Todo task did not retain a clean zero-attachment detail state.");
        }
    }
}

internal sealed class AotManagedUiTodoAttachmentsPersistenceEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool InitialAttachmentUiProjected { get; set; }
    public bool RestartAttachmentUiProjected { get; set; }
    public bool ManagedAttachmentDeleted { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public string ManagedAttachmentPath { get; set; } = string.Empty;
    public AotManagedUiTodoStateEvidence Before { get; set; } = new();
    public AotManagedUiTodoStateEvidence? AfterAttachmentDelete { get; set; }
    public AotManagedUiTodoStateEvidence After { get; set; } = new();
}
#endif
