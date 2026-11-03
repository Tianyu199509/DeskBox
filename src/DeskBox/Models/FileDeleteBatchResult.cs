namespace DeskBox.Models;

public sealed record FileDeleteFailure(
    string Path,
    string Name,
    string ErrorMessage);

public sealed record FileDeleteBatchResult(
    int DeletedCount,
    IReadOnlyList<FileDeleteFailure> Failures,
    int PermanentlyDeletedCount = 0)
{
    public bool IsComplete => Failures.Count == 0;
}
