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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Exception> _lastItemErrors =
        new(StringComparer.OrdinalIgnoreCase);

    internal Exception? TryGetLastItemError(string sourcePath) =>
        _lastItemErrors.TryGetValue(sourcePath, out Exception? error) ? error : null;

    public async Task<IReadOnlyList<FileService.FileTransferResult>> MoveAsync(
        IReadOnlyList<FileService.FileTransferPlan> plans,
        bool publicDesktop,
        IntPtr ownerWindowHandle,
        Action<FileService.FileTransferResult> itemCompleted,
        CancellationToken cancellationToken)
    {
        _lastItemErrors.Clear();
        var results = await files.ExecuteTransferPlanAsync(
            plans,
            move: true,
            useShellProgress: publicDesktop,
            ownerWindowHandle: ownerWindowHandle,
            cancellationToken: cancellationToken,
            allowShellElevation: publicDesktop,
            itemCompleted: itemCompleted,
            onItemError: publicDesktop
                ? null
                : error =>
                {
                    _lastItemErrors[error.SourcePath] = error.Exception;
                    return Task.FromResult(FileService.FileTransferItemAction.Skip);
                });
        return results;
    }
}

public sealed class DesktopOrganizationIncompleteUndoException(int restoredCount, int remainingCount)
    : IOException($"Restored {restoredCount} item(s); {remainingCount} item(s) still need restoration.")
{
    public int RestoredCount { get; } = restoredCount;
    public int RemainingCount { get; } = remainingCount;
}

/// <summary>Surfaced verbatim in the UI; the generic failure text would hide the cause.</summary>
public sealed class DesktopOrganizationPendingRecoveryException()
    : InvalidOperationException("A pending desktop operation must be recovered first.");

/// <summary>Surfaced verbatim in the UI; the generic failure text would hide the cause.</summary>
public sealed class DesktopOrganizationInsufficientSpaceException(string driveName)
    : IOException($"There is not enough free space on {driveName}.")
{
    public string DriveName { get; } = driveName;
}

/// <summary>Surfaced verbatim in the UI; the generic failure text would hide the cause.</summary>
public sealed class DesktopOrganizationInvalidStorageRootException(string message)
    : InvalidOperationException(message);
