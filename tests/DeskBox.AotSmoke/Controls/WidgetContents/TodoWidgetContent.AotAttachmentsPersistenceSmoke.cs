#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private const string AotTodoAttachmentsTaskTitle =
        "AOT Todo managed attachment task";

    internal async Task<AotTodoAttachmentMutationResult>
        RunAotTodoAttachmentMutationAsync(string fixturePath)
    {
        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException(
                "The owned Todo attachment fixture is unavailable.",
                fixturePath);
        }
        if (ViewModel is null || !ViewModel.IsInitialized)
        {
            throw new InvalidOperationException(
                "The Todo attachment mutation surface is not initialized.");
        }
        if (ViewModel.Items.Count != 0)
        {
            throw new InvalidOperationException(
                "The Todo attachment mutation phase did not start from an empty store.");
        }

        await OpenAddEditorAsync();
        DetailTitleTextBox.Text = AotTodoAttachmentsTaskTitle;
        TodoItemViewModel item = await ViewModel.FinalizeDetailAsync(
            DetailTitleTextBox.Text,
            closeDetail: false) ??
            throw new InvalidOperationException(
                "The real Todo detail path did not persist the attachment fixture task.");

        TodoAttachmentViewModel attachment =
            await ViewModel.AddAttachmentPathAsync(
                item.Id,
                fixturePath,
                copyToManagedStorageOverride: true) ??
            throw new InvalidOperationException(
                "The product Todo managed attachment import path returned no attachment.");
        if (!attachment.IsManagedCopy || !File.Exists(attachment.FilePath))
        {
            throw new InvalidOperationException(
                "The Todo attachment was not copied into managed storage.");
        }

        AotAttachmentTileObservation tile =
            await DetailAttachmentStrip.WaitForAotAttachmentTileAsync(
                attachment.Id,
                attachment.DisplayName);
        bool initialAttachmentUiProjected = string.Equals(
            tile.Attachment.Id,
            attachment.Id,
            StringComparison.Ordinal);
        if (!initialAttachmentUiProjected)
        {
            throw new InvalidOperationException(
                "The real Todo attachment tile did not expose the imported attachment.");
        }

        return new AotTodoAttachmentMutationResult(
            item.Id,
            attachment.Id,
            attachment.FilePath,
            initialAttachmentUiProjected);
    }

    internal async Task<bool> WaitForAotTodoAttachmentProjectionAsync(
        string itemId,
        string attachmentId)
    {
        TodoItemViewModel item = await OpenAotTodoItemAsync(itemId);
        TodoAttachmentViewModel attachment = item.Attachments.Single(candidate =>
            string.Equals(candidate.Id, attachmentId, StringComparison.Ordinal));
        AotAttachmentTileObservation tile =
            await DetailAttachmentStrip.WaitForAotAttachmentTileAsync(
                attachment.Id,
                attachment.DisplayName);
        return string.Equals(
            tile.Attachment.Id,
            attachmentId,
            StringComparison.Ordinal) &&
            tile.Attachment.IsManagedCopy &&
            tile.Attachment.Exists;
    }

    internal async Task<AotTodoAttachmentDeleteResult>
        DeleteAotTodoManagedAttachmentAsync(
            string itemId,
            string attachmentId)
    {
        TodoItemViewModel item = await OpenAotTodoItemAsync(itemId);
        TodoAttachmentViewModel attachment = item.Attachments.Single(candidate =>
            string.Equals(candidate.Id, attachmentId, StringComparison.Ordinal));
        AotAttachmentTileObservation tile =
            await DetailAttachmentStrip.WaitForAotAttachmentTileAsync(
                attachment.Id,
                attachment.DisplayName);
        if (!tile.Attachment.IsManagedCopy)
        {
            throw new InvalidOperationException(
                "The reloaded Todo attachment is not a managed copy.");
        }

        string managedPath = tile.Attachment.FilePath;
        bool deleted = await DeleteDetailAttachmentAsync(tile.Attachment);
        await DetailAttachmentStrip.WaitForAotAttachmentTileEmptyAsync();
        bool managedAttachmentDeleted =
            deleted &&
            item.Attachments.Count == 0 &&
            item.Item.Attachments.Count == 0 &&
            !File.Exists(managedPath);
        if (!managedAttachmentDeleted)
        {
            throw new InvalidOperationException(
                "The product Todo attachment delete path left metadata, UI, or the managed file behind.");
        }

        return new AotTodoAttachmentDeleteResult(
            item.Id,
            attachment.Id,
            managedPath,
            managedAttachmentDeleted);
    }
}

internal sealed record AotTodoAttachmentMutationResult(
    string ItemId,
    string AttachmentId,
    string ManagedAttachmentPath,
    bool InitialAttachmentUiProjected);

internal sealed record AotTodoAttachmentDeleteResult(
    string ItemId,
    string AttachmentId,
    string ManagedAttachmentPath,
    bool ManagedAttachmentDeleted);
#endif
