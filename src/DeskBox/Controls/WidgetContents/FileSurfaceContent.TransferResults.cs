using DeskBox.Services;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private static int AppendTransferResults(
        List<FileService.FileTransferResult> destination,
        IReadOnlyList<FileService.FileTransferResult> batch)
    {
        destination.AddRange(batch);
        return (batch as FileService.FileTransferBatchResult)?.SkippedSourcePaths.Count ?? 0;
    }

    private bool ShowSkippedTransferFeedback(int skippedCount)
    {
        if (skippedCount == 0)
        {
            return false;
        }

        _lastImportShowedDedicatedWarning = true;
        ShowFeedback(new(
            _localizationService.Format("Widget.Import.UserSkipped", skippedCount),
            DeskBox.Models.WidgetFeedbackSeverity.Info,
            "file-transfer-user-skipped"));
        return true;
    }
}
