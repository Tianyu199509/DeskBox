using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Controls;

/// <summary>
/// Creates the shared Quick Capture drag payload for standalone and grouped
/// surfaces. The visible selection is authoritative when the event anchor is
/// one of its members.
/// </summary>
public static class QuickCaptureDragPackage
{
    public static int ResolveManualDropTargetIndex(
        IReadOnlyList<QuickCaptureItemViewModel> currentItems,
        string? draggedItemId,
        string? targetItemId,
        bool insertAfter)
    {
        if (string.IsNullOrWhiteSpace(draggedItemId) ||
            string.IsNullOrWhiteSpace(targetItemId))
        {
            return -1;
        }

        int currentIndex = -1;
        int targetIndex = -1;
        for (int index = 0; index < currentItems.Count; index++)
        {
            QuickCaptureItemViewModel item = currentItems[index];
            if (string.Equals(item.Id, draggedItemId, StringComparison.Ordinal))
            {
                currentIndex = index;
            }

            if (string.Equals(item.Id, targetItemId, StringComparison.Ordinal))
            {
                targetIndex = index;
            }
        }

        if (currentIndex < 0 || targetIndex < 0)
        {
            return -1;
        }

        int insertionBoundary = targetIndex + (insertAfter ? 1 : 0);
        return insertionBoundary - (currentIndex < insertionBoundary ? 1 : 0);
    }

    public static IReadOnlyList<QuickCaptureItemViewModel> ResolveDraggedItems(
        IReadOnlyList<QuickCaptureItemViewModel> eventItems,
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems)
    {
        QuickCaptureItemViewModel[] distinctEventItems = eventItems.Distinct().ToArray();
        QuickCaptureItemViewModel[] distinctSelectedItems = selectedItems.Distinct().ToArray();
        if (distinctSelectedItems.Length <= 1 || distinctEventItems.Length == 0)
        {
            return distinctEventItems;
        }

        return distinctEventItems.Any(distinctSelectedItems.Contains)
            ? distinctSelectedItems
            : distinctEventItems;
    }

    public static bool TryPrepare(
        DataPackage dataPackage,
        IReadOnlyList<QuickCaptureItemViewModel> draggedItems,
        LocalizationService localizationService)
    {
        ArgumentNullException.ThrowIfNull(dataPackage);
        ArgumentNullException.ThrowIfNull(localizationService);
        if (draggedItems.Count == 0)
        {
            return false;
        }

        dataPackage.RequestedOperation = DataPackageOperation.Copy;
        if (draggedItems.Count > 1)
        {
            string text = QuickCaptureClipboardFormatter.FormatBatch(
                draggedItems,
                localizationService);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            DeskBoxDragData.SetText(
                dataPackage,
                text,
                DeskBoxDragData.SourceQuickCapture);
            dataPackage.Properties.Title = localizationService.Format(
                "QuickCapture.CopiedCount",
                draggedItems.Count);
            return true;
        }

        QuickCaptureItemViewModel item = draggedItems[0];
        if (item.Type == QuickCaptureItemType.Image &&
            !string.IsNullOrWhiteSpace(item.ImagePath) &&
            File.Exists(item.ImagePath))
        {
            string imagePath = Path.GetFullPath(item.ImagePath);
            dataPackage.SetDataProvider(StandardDataFormats.Bitmap, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                    request.SetData(
                        Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                }
                catch (Exception ex)
                {
                    App.Log($"[QuickCapture] Failed to provide dragged bitmap: {ex}");
                }
                finally
                {
                    deferral.Complete();
                }
            });

            dataPackage.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                    request.SetData(new List<IStorageItem> { file });
                }
                catch (Exception ex)
                {
                    App.Log($"[QuickCapture] Failed to provide dragged image: {ex}");
                }
                finally
                {
                    deferral.Complete();
                }
            });
            if (!string.IsNullOrWhiteSpace(item.Body) &&
                !string.Equals(item.Body, "Image", StringComparison.Ordinal))
            {
                DeskBoxDragData.SetText(
                    dataPackage,
                    item.Body,
                    DeskBoxDragData.SourceQuickCapture);
            }

            dataPackage.Properties.Title = Path.GetFileName(imagePath);
            return true;
        }

        if (item.Type == QuickCaptureItemType.Link &&
            Uri.TryCreate(item.Url ?? item.Body, UriKind.Absolute, out Uri? uri))
        {
            DeskBoxDragData.SetText(
                dataPackage,
                item.Body,
                DeskBoxDragData.SourceQuickCapture);
            dataPackage.SetWebLink(uri);
            dataPackage.SetUri(uri);
            dataPackage.Properties.Title = item.Body;
            return true;
        }

        string textValue = QuickCaptureClipboardFormatter.FormatSingle(
            item,
            localizationService);
        if (string.IsNullOrWhiteSpace(textValue))
        {
            return false;
        }

        DeskBoxDragData.SetText(
            dataPackage,
            textValue,
            DeskBoxDragData.SourceQuickCapture);
        dataPackage.Properties.Title = item.DisplayText;
        return true;
    }
}
