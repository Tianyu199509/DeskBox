using DeskBox.Platform;

namespace DeskBox.Services.DesktopOrganization;

internal interface IDesktopIconPositionBackend
{
    IReadOnlyDictionary<string, DesktopIconPoint> TryCaptureDesktopPositions(
        IReadOnlyCollection<string> normalizedPaths);

    IReadOnlySet<string> TryRestoreDesktopPositions(
        IReadOnlyDictionary<string, DesktopIconPoint> desiredPositionsByPath);
}

internal sealed class ShellDesktopIconPositionBackend : IDesktopIconPositionBackend
{
    public IReadOnlyDictionary<string, DesktopIconPoint> TryCaptureDesktopPositions(
        IReadOnlyCollection<string> normalizedPaths) =>
        DesktopIconPositionInterop.TryCaptureDesktopPositions(normalizedPaths);

    public IReadOnlySet<string> TryRestoreDesktopPositions(
        IReadOnlyDictionary<string, DesktopIconPoint> desiredPositionsByPath) =>
        DesktopIconPositionInterop.TryRestoreDesktopPositions(desiredPositionsByPath);
}

internal sealed class DesktopIconCaptureSnapshot
{
    internal static DesktopIconCaptureSnapshot Empty { get; } =
        new(new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase));

    internal DesktopIconCaptureSnapshot(IReadOnlyDictionary<string, DesktopIconPoint> positionsBySourcePath)
    {
        PositionsBySourcePath = positionsBySourcePath;
    }

    internal IReadOnlyDictionary<string, DesktopIconPoint> PositionsBySourcePath { get; }

    internal bool IsEmpty => PositionsBySourcePath.Count == 0;
}

/// <summary>
/// Captures Windows desktop icon coordinates right before managed organization
/// moves items off the desktop and restores those coordinates when the items
/// are later moved back to the desktop, undone, or recovered.
/// </summary>
public sealed class DesktopIconPositionService
{
    private readonly DesktopIconPositionLedgerStore _ledger;
    private readonly IDesktopIconPositionBackend _backend;
    private readonly Func<string> _personalDesktopPathProvider;
    private readonly Func<string> _publicDesktopPathProvider;

    public DesktopIconPositionService()
        : this(
            new DesktopIconPositionLedgerStore(),
            new ShellDesktopIconPositionBackend(),
            static () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            static () => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory))
    {
    }

    internal DesktopIconPositionService(
        DesktopIconPositionLedgerStore ledger,
        IDesktopIconPositionBackend backend,
        Func<string> personalDesktopPathProvider,
        Func<string> publicDesktopPathProvider)
    {
        _ledger = ledger;
        _backend = backend;
        _personalDesktopPathProvider = personalDesktopPathProvider;
        _publicDesktopPathProvider = publicDesktopPathProvider;
    }

    internal async Task<DesktopIconCaptureSnapshot> CaptureBeforeMoveFromDesktopAsync(
        IReadOnlyList<FileService.FileTransferPlan> plans,
        CancellationToken cancellationToken = default)
    {
        if (plans is null || plans.Count == 0)
        {
            return DesktopIconCaptureSnapshot.Empty;
        }

        try
        {
            var desktopSources = new List<string>();
            foreach (FileService.FileTransferPlan plan in plans)
            {
                string? src = DesktopIconPositionLedgerStore.NormalizePath(plan.SourcePath);
                string? dst = DesktopIconPositionLedgerStore.NormalizePath(plan.DestinationPath);
                if (src is null || dst is null)
                {
                    continue;
                }

                if (IsDirectDesktopEntry(src) && !IsDirectDesktopEntry(dst))
                {
                    desktopSources.Add(src);
                }
            }

            if (desktopSources.Count == 0)
            {
                return DesktopIconCaptureSnapshot.Empty;
            }

            IReadOnlyDictionary<string, DesktopIconPoint> captured = await Task.Run(
                () => _backend.TryCaptureDesktopPositions(desktopSources),
                cancellationToken).ConfigureAwait(false);
            return captured.Count == 0
                ? DesktopIconCaptureSnapshot.Empty
                : new DesktopIconCaptureSnapshot(captured);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopIconPosition] Pre-move capture skipped: {ex.Message}");
            return DesktopIconCaptureSnapshot.Empty;
        }
    }

    internal async Task CommitAfterMoveOffDesktopAsync(
        DesktopIconCaptureSnapshot capture,
        IReadOnlyList<FileService.FileTransferResult> results)
    {
        if (results is null || results.Count == 0)
        {
            return;
        }

        try
        {
            var newRecords = new List<(string CurrentPath, string OriginalDesktopPath, DesktopIconPoint Point)>();
            var relocations = new List<(string OldPath, string NewPath)>();

            foreach (FileService.FileTransferResult result in results)
            {
                string? src = DesktopIconPositionLedgerStore.NormalizePath(result.SourcePath);
                string? dst = DesktopIconPositionLedgerStore.NormalizePath(result.DestinationPath);
                if (src is null || dst is null)
                {
                    continue;
                }

                if (!capture.IsEmpty &&
                    capture.PositionsBySourcePath.TryGetValue(src, out DesktopIconPoint point) &&
                    !IsDirectDesktopEntry(dst))
                {
                    newRecords.Add((dst, src, point));
                }
                else if (!IsDirectDesktopEntry(src) && !IsDirectDesktopEntry(dst))
                {
                    relocations.Add((src, dst));
                }
            }

            if (newRecords.Count > 0)
            {
                await _ledger.RecordMovesFromDesktopAsync(newRecords).ConfigureAwait(false);
            }

            if (relocations.Count > 0)
            {
                await _ledger.RelocatePathsAsync(relocations).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopIconPosition] Post-move commit skipped: {ex.Message}");
        }
    }

    internal async Task RelocateMovesAsync(IReadOnlyList<FileService.FileTransferResult> results)
    {
        if (results is null || results.Count == 0)
        {
            return;
        }

        try
        {
            var moves = new List<(string OldPath, string NewPath)>(results.Count);
            foreach (FileService.FileTransferResult result in results)
            {
                string? src = DesktopIconPositionLedgerStore.NormalizePath(result.SourcePath);
                string? dst = DesktopIconPositionLedgerStore.NormalizePath(result.DestinationPath);
                if (src is not null && dst is not null && !IsDirectDesktopEntry(dst))
                {
                    moves.Add((src, dst));
                }
            }

            if (moves.Count > 0)
            {
                await _ledger.RelocatePathsAsync(moves).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopIconPosition] Relocate skipped: {ex.Message}");
        }
    }

    internal async Task RelocateDirectoryPrefixAsync(string? oldDirectoryPath, string? newDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(oldDirectoryPath) ||
            string.IsNullOrWhiteSpace(newDirectoryPath))
        {
            return;
        }

        try
        {
            await _ledger.RelocateDirectoryPrefixAsync(oldDirectoryPath, newDirectoryPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopIconPosition] Directory prefix relocate skipped: {ex.Message}");
        }
    }

    internal async Task RestoreAfterMoveToDesktopAsync(
        IReadOnlyList<FileService.FileTransferResult> results)
    {
        if (results is null || results.Count == 0)
        {
            return;
        }

        try
        {
            var candidates = new List<(string SourcePath, string DestinationPath)>();
            foreach (FileService.FileTransferResult result in results)
            {
                string? src = DesktopIconPositionLedgerStore.NormalizePath(result.SourcePath);
                string? dst = DesktopIconPositionLedgerStore.NormalizePath(result.DestinationPath);
                if (src is not null && dst is not null && IsDirectDesktopEntry(dst))
                {
                    candidates.Add((src, dst));
                }
            }

            if (candidates.Count == 0)
            {
                return;
            }

            IReadOnlyDictionary<string, DesktopIconPositionRecord> records =
                await _ledger.LookupByCurrentPathsAsync(candidates.Select(pair => pair.SourcePath))
                    .ConfigureAwait(false);
            if (records.Count == 0)
            {
                return;
            }

            var desiredByDestination = new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);
            var sourceKeyByDestination = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach ((string src, string dst) in candidates)
            {
                if (!records.TryGetValue(src, out DesktopIconPositionRecord? record))
                {
                    continue;
                }

                // Refuse to claim an old desktop slot when conflict resolution
                // renamed the restored file (e.g. "WeChat (2).lnk" vs "WeChat.lnk").
                string originalName = Path.GetFileName(record.OriginalDesktopPath);
                string destinationName = Path.GetFileName(dst);
                if (!string.Equals(originalName, destinationName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                desiredByDestination[dst] = new DesktopIconPoint(record.X, record.Y);
                sourceKeyByDestination[dst] = src;
            }

            if (desiredByDestination.Count == 0)
            {
                return;
            }

            IReadOnlySet<string> restoredDestinations = await Task.Run(
                () => _backend.TryRestoreDesktopPositions(desiredByDestination))
                .ConfigureAwait(false);

            if (restoredDestinations.Count == 0)
            {
                return;
            }

            var consumedKeys = new List<string>(restoredDestinations.Count);
            foreach (string destPath in restoredDestinations)
            {
                if (sourceKeyByDestination.TryGetValue(destPath, out string? sourceKey))
                {
                    consumedKeys.Add(sourceKey);
                }
            }

            if (consumedKeys.Count > 0)
            {
                await _ledger.RemoveByCurrentPathsAsync(consumedKeys).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopIconPosition] Post-restore placement skipped: {ex.Message}");
        }
    }

    internal void ForgetEntriesInBackground(IEnumerable<string> paths)
    {
        string[] snapshot = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _ledger.RemoveByCurrentPathsAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.LogVerbose($"[DesktopIconPosition] Forget entries skipped: {ex.Message}");
            }
        });
    }

    internal Task ForgetEntriesAsync(IEnumerable<string> paths) =>
        _ledger.RemoveByCurrentPathsAsync(paths);

    internal bool IsDirectDesktopEntry(string normalizedPath)
    {
        string? parent = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        string? normalizedParent = DesktopIconPositionLedgerStore.NormalizePath(parent);
        if (normalizedParent is null)
        {
            return false;
        }

        string? personalDesktop = DesktopIconPositionLedgerStore.NormalizePath(_personalDesktopPathProvider());
        if (personalDesktop is not null &&
            string.Equals(normalizedParent, personalDesktop, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? publicDesktop = DesktopIconPositionLedgerStore.NormalizePath(_publicDesktopPathProvider());
        return publicDesktop is not null &&
               string.Equals(normalizedParent, publicDesktop, StringComparison.OrdinalIgnoreCase);
    }
}
