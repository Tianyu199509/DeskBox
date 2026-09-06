namespace DeskBox.Services;

/// <summary>Interactive public transfers use one Shell batch; the host stays unelevated.</summary>
public interface IDesktopOrganizationTransfer
{
    Task<IReadOnlyList<FileService.FileTransferResult>> MoveAsync(
        IReadOnlyList<FileService.FileTransferPlan> plans,
        bool publicDesktop,
        IntPtr ownerWindowHandle,
        Action<FileService.FileTransferResult> itemCompleted,
        CancellationToken cancellationToken);
}

internal sealed class DesktopOrganizationTransfer(FileService files) : IDesktopOrganizationTransfer
{
    public async Task<IReadOnlyList<FileService.FileTransferResult>> MoveAsync(
        IReadOnlyList<FileService.FileTransferPlan> plans,
        bool publicDesktop,
        IntPtr ownerWindowHandle,
        Action<FileService.FileTransferResult> itemCompleted,
        CancellationToken cancellationToken)
    {
        var results = await files.ExecuteTransferPlanAsync(
            plans, move: true, useShellProgress: publicDesktop,
            ownerWindowHandle: ownerWindowHandle, cancellationToken: cancellationToken,
            allowShellElevation: publicDesktop,
            itemCompleted: publicDesktop ? itemCompleted : null);
        if (!publicDesktop)
            foreach (var result in results) itemCompleted(result);
        return results;
    }
}

public sealed class DesktopOrganizationIncompleteUndoException(int restoredCount, int remainingCount)
    : IOException($"Restored {restoredCount} item(s); {remainingCount} item(s) still need restoration.")
{
    public int RestoredCount { get; } = restoredCount;
    public int RemainingCount { get; } = remainingCount;
}
