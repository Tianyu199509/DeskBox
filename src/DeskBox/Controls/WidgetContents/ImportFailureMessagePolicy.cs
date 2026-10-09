using DeskBox.Services;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Pure import-failure message selection shared by every import UI leg and
/// its behavioral tests. Transfer-exception messages are English diagnostics
/// aimed at the log and must not reach the toast; this policy maps exception
/// identity to the localized key (plus optional format args) the user sees.
/// </summary>
internal static class ImportFailureMessagePolicy
{
    /// <summary>
    /// Picks a user-facing override for the exception, or null when the
    /// caller should fall back to the partial-failure / generic wording.
    /// </summary>
    public static (string Key, object[] Args)? SelectOverride(
        Exception exception,
        int requestedCount,
        string? singleItemPath)
    {
        if (exception is OrganizerService.MappedFolderUnavailableException)
        {
            return ("Widget.Import.MappedFolderUnavailable", []);
        }

        if (exception is OrganizerService.DestinationOutsideMappedRootException)
        {
            return ("Widget.Import.DestinationOutsideMappedRoot", []);
        }

        // A transfer that completed nothing and whose cause is a source file
        // held open by another program: say so instead of the generic
        // import-failed wording. Partial results keep the count message —
        // one representative HRESULT cannot honestly label the rest.
        bool hasCompletedResults =
            exception is FileService.IFileTransferWithCompletedResults
                { CompletedResults.Count: > 0 };
        if (!hasCompletedResults &&
            FileService.ClassifyTransferError(exception) ==
                FileService.FileTransferItemErrorKind.InUse)
        {
            return requestedCount == 1 && !string.IsNullOrEmpty(singleItemPath)
                ? ("Widget.Error.FileInUseWithPath", new object[] { singleItemPath })
                : ("Widget.Error.FileInUse", []);
        }

        return null;
    }
}
