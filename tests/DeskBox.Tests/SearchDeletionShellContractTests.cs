namespace DeskBox.Tests;

/// <summary>
/// Pins the search popup's deletion shape after the 2026-10-11 file-safety
/// audit (MF-P1-02): deletions must go through the Shell with an owner
/// window so the native confirmation speaks for the real outcome, and the
/// retired VB delete path (whose silent permanent delete on volumes without
/// a Recycle Bin contradicted the self-drawn dialog's promise) must not
/// come back.
/// </summary>
public sealed class SearchDeletionShellContractTests
{
    [Fact]
    public void SearchDeletion_RoutesThroughShellWithOwnerWindow()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Views/SearchPopupWindow.ResultsAndPreview.cs");

        Assert.Contains(
            "DeleteEntriesWithShellAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "recycle: true,",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SearchDeletion_DoesNotUseVisualBasicOrSelfDrawnRecyclePromise()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Views/SearchPopupWindow.ResultsAndPreview.cs");

        Assert.DoesNotContain("Microsoft.VisualBasic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Search.Delete.Title", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Search.Delete.Message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Search.Batch.DeleteTitle", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "DeskBox")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("DeskBox repository root was not found.");
        }

        return File.ReadAllText(Path.Combine(
            directory.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
