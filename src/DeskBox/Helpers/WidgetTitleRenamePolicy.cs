namespace DeskBox.Helpers;

/// <summary>
/// Pure commit and recovery decisions for widget title inline rename.
/// Keeps unchanged/empty blurs from hitting storage validation and prevents
/// <c>LostFocus</c> validation failures from re-focusing in a 60 Hz loop.
/// </summary>
internal static class WidgetTitleRenamePolicy
{
    public static bool ShouldSkipCommitAsUnchanged(
        string? candidateText,
        string? initialDisplayText)
    {
        string trimmedCandidate = (candidateText ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmedCandidate))
        {
            return true;
        }

        string trimmedInitial = (initialDisplayText ?? string.Empty).Trim();
        return string.Equals(trimmedCandidate, trimmedInitial, StringComparison.Ordinal);
    }

    public static bool ShouldKeepEditorOpenAfterFailure(
        bool initiatedByBlur,
        bool isWindowVisible,
        bool isClosing,
        bool isHideInProgress,
        bool ownsForeground)
    {
        return !initiatedByBlur &&
               isWindowVisible &&
               !isClosing &&
               !isHideInProgress &&
               ownsForeground;
    }
}
