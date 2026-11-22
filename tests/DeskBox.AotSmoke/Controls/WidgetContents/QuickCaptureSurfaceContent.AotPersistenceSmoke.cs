#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureSurfaceContent
{
    internal AotQuickCaptureSurfaceSnapshot CaptureAotQuickCaptureSurfaceSnapshot()
    {
        return new AotQuickCaptureSurfaceSnapshot(
            _isInitialized,
            IsLoaded,
            XamlRoot is not null,
            ViewModel.Items.Count,
            _detailItem?.Id,
            _detailItem?.Body ?? string.Empty,
            _isCreatingDetail,
            _isDetailEditing,
            _detailHasUnsavedChanges,
            _pendingDetailAttachments.Count);
    }

    internal async Task<AotQuickCaptureMutationResult>
        RunAotQuickCaptureMutationAsync(
            string pendingSaveBody,
            string autoSaveBody,
            string attachmentFixturePath)
    {
        if (!File.Exists(attachmentFixturePath))
        {
            throw new FileNotFoundException(
                "The owned Quick Capture attachment fixture is unavailable.",
                attachmentFixturePath);
        }

        await ViewModel.RefreshItemsAsync();
        if (ViewModel.Items.Count != 0)
        {
            throw new InvalidOperationException(
                "The Quick Capture mutation phase did not start from an empty store.");
        }

        await OpenNewDetailAsync();
        SetDetailEditorText(pendingSaveBody);
        MarkDetailDirty();
        if (!HasNewDetailContent())
        {
            throw new InvalidOperationException(
                "The owned Quick Capture draft was not meaningful.");
        }

        await FlushPendingDetailSaveAsync();
        bool pendingSaveFlushed =
            !_detailHasUnsavedChanges &&
            !_isCreatingDetail &&
            _detailItem is not null &&
            string.Equals(_detailItem.Body, pendingSaveBody, StringComparison.Ordinal);
        if (!pendingSaveFlushed)
        {
            throw new InvalidOperationException(
                "The meaningful Quick Capture draft was not flushed through the product path.");
        }

        string itemId = _detailItem!.Id;
        SetDetailEditorText(autoSaveBody);
        MarkDetailDirty();
        long autoSaveRevision = _detailEditRevision;
        ScheduleDetailAutoSave();
        bool autoSaveObserved = await WaitForAotQuickCaptureAutoSaveAsync(
            itemId,
            autoSaveBody,
            autoSaveRevision);
        if (!autoSaveObserved)
        {
            throw new InvalidOperationException(
                "The real Quick Capture 600 ms auto-save timer did not persist the edit.");
        }

        DroppedFilePath[] attachment =
        [
            new DroppedFilePath(
                attachmentFixturePath,
                Path.GetFileName(attachmentFixturePath),
                ForceManagedCopy: true)
        ];
        _detailItem = await ViewModel.AddAttachmentsAsync(_detailItem!, attachment) ??
            throw new InvalidOperationException(
                "The product Quick Capture attachment path did not return the updated item.");
        RefreshDetailAttachments();

        TodoAttachmentViewModel managedAttachment = _detailItem.Attachments.Single();
        if (!managedAttachment.IsManagedCopy || !File.Exists(managedAttachment.FilePath))
        {
            throw new InvalidOperationException(
                "The Quick Capture attachment was not copied into managed storage.");
        }

        return new AotQuickCaptureMutationResult(
            itemId,
            pendingSaveFlushed,
            autoSaveObserved,
            managedAttachment.FilePath);
    }

    internal async Task<bool> FlushAotQuickCaptureExistingItemAsync(
        string itemId,
        string body)
    {
        await OpenAotQuickCaptureItemAsync(itemId);
        SetDetailEditorText(body);
        MarkDetailDirty();
        await FlushPendingDetailSaveAsync();
        return !_detailHasUnsavedChanges &&
            _detailItem is not null &&
            string.Equals(_detailItem.Body, body, StringComparison.Ordinal);
    }

    internal async Task<string> DeleteAotQuickCaptureManagedAttachmentAsync(
        string itemId)
    {
        await OpenAotQuickCaptureItemAsync(itemId);
        QuickCaptureItemViewModel item = _detailItem ??
            throw new InvalidOperationException(
                "The owned Quick Capture item is not open for attachment deletion.");
        TodoAttachmentViewModel attachment = item.Attachments.Single();
        if (!attachment.IsManagedCopy)
        {
            throw new InvalidOperationException(
                "The owned Quick Capture attachment is not a managed copy.");
        }

        string managedPath = attachment.FilePath;
        _detailItem = await ViewModel.DeleteAttachmentAsync(item, attachment.Id) ??
            throw new InvalidOperationException(
                "The product Quick Capture attachment delete path did not return an item.");
        RefreshDetailAttachments();
        ApplyDetailMaterialSurface();
        if (File.Exists(managedPath))
        {
            throw new InvalidOperationException(
                "The product Quick Capture attachment delete path left the managed file behind.");
        }

        return managedPath;
    }

    internal async Task DeleteAotQuickCaptureItemAsync(string itemId)
    {
        await OpenAotQuickCaptureItemAsync(itemId);
        QuickCaptureItemViewModel item = _detailItem ??
            throw new InvalidOperationException(
                "The owned Quick Capture item is not open for deletion.");
        await DeleteQuickCaptureItemAsync(item);
        await ViewModel.RefreshItemsAsync();
        if (ViewModel.Items.Any(candidate =>
                string.Equals(candidate.Id, itemId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The product Quick Capture item delete path left the record visible.");
        }
    }

    internal async Task OpenAotQuickCaptureItemAsync(string itemId)
    {
        await ViewModel.RefreshItemsAsync();
        QuickCaptureItemViewModel item = ViewModel.Items.Single(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        await OpenDetailAfterSavingAsync(item);
        if (!_isDetailEditing)
        {
            _ = BeginDetailEditing();
        }
        if (_detailItem is null ||
            !string.Equals(_detailItem.Id, itemId, StringComparison.Ordinal) ||
            !_isDetailEditing)
        {
            throw new InvalidOperationException(
                "The owned Quick Capture detail editor could not be opened.");
        }
    }

    private async Task<bool> WaitForAotQuickCaptureAutoSaveAsync(
        string itemId,
        string expectedBody,
        long expectedRevision)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!_detailHasUnsavedChanges &&
                !_isSavingDetail &&
                _detailSavedRevision >= expectedRevision &&
                _detailItem is not null &&
                string.Equals(_detailItem.Id, itemId, StringComparison.Ordinal) &&
                string.Equals(_detailItem.Body, expectedBody, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }
}

internal sealed record AotQuickCaptureSurfaceSnapshot(
    bool IsInitialized,
    bool IsLoaded,
    bool HasXamlRoot,
    int SurfaceItemCount,
    string? DetailItemId,
    string DetailBody,
    bool IsCreatingDetail,
    bool IsDetailEditing,
    bool DetailHasUnsavedChanges,
    int PendingAttachmentCount);

internal sealed record AotQuickCaptureMutationResult(
    string ItemId,
    bool PendingSaveFlushed,
    bool AutoSaveObserved,
    string ManagedAttachmentPath);
#endif
