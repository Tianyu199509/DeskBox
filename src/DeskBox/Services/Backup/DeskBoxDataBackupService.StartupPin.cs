using DeskBox.FileSafety;

namespace DeskBox.Services;

public sealed partial class DeskBoxDataBackupService
{
    private const long MaxPinnedStartupJsonBytes = 16L * 1024 * 1024;

    private static readonly string[] s_fileSafetyMetadataNames =
    [
        "settings.json",
        "widget-layout.json",
        "desktop-organization-history.json",
        "desktop-organization-recovery.json"
    ];

    private readonly object _startupMetadataPinSync = new();
    private Dictionary<string, byte[]?>? _pinnedStartupMetadataSnapshot;

    /// <summary>
    /// Captures an in-memory byte snapshot of top-level JSON stores when a scheduled
    /// startup automatic backup is due, completing in a few milliseconds so desktop
    /// widget restoration can proceed immediately while heavy attachment copying and
    /// ZIP compression continue in the background without post-restore state tearing.
    /// </summary>
    internal async Task CaptureStartupMetadataPinIfDueAsync(
        CancellationToken cancellationToken = default)
    {
        ClearStartupMetadataPin();

        if (IsRecoveryOfferPauseActive)
        {
            return;
        }

        AutomaticBackupOptions options = _automaticBackupOptions;
        if (!options.IsEnabled)
        {
            return;
        }

        TimeSpan minInterval = TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes));
        AutomaticSnapshotTarget candidateTarget = ResolveAutomaticSnapshotTarget(probeWriteAccess: false);
        if (HasRecentAutomaticSnapshot(candidateTarget.Directory, minInterval) ||
            !HasBackupSourceData())
        {
            return;
        }

        var captured = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        await DesktopOrganizationTransaction.OperationGate.WaitAsync(cancellationToken);
        try
        {
            SemaphoreSlim settingsWriteLock = SettingsService.FileWriteLockFor(DataDirectory);
            await settingsWriteLock.WaitAsync(cancellationToken);
            try
            {
                if (!Directory.Exists(DataDirectory))
                {
                    return;
                }

                var candidateNames = new HashSet<string>(s_fileSafetyMetadataNames, StringComparer.OrdinalIgnoreCase);
                foreach (string jsonPath in Directory.EnumerateFiles(DataDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(jsonPath);
                    if (!string.IsNullOrWhiteSpace(fileName) && ShouldIncludeInBackup(fileName))
                    {
                        candidateNames.Add(fileName);
                    }
                }

                foreach (string relativePath in candidateNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string sourcePath = Path.Combine(DataDirectory, relativePath);
                    if (!File.Exists(sourcePath))
                    {
                        // Record genuine pre-restore absence so post-restore newly-created
                        // metadata files are not folded into the pre-restore snapshot.
                        captured[relativePath] = null;
                        continue;
                    }

                    var info = new FileInfo(sourcePath);
                    if (info.Length > MaxPinnedStartupJsonBytes)
                    {
                        // Leave oversized JSON stores unpinned so streaming copy handles them.
                        continue;
                    }

                    byte[]? bytes = await TryReadStableSnapshotBytesAsync(sourcePath, cancellationToken);
                    if (bytes is not null)
                    {
                        captured[relativePath] = bytes;
                    }
                }
            }
            finally
            {
                settingsWriteLock.Release();
            }
        }
        finally
        {
            DesktopOrganizationTransaction.OperationGate.Release();
        }

        lock (_startupMetadataPinSync)
        {
            _pinnedStartupMetadataSnapshot = captured;
        }
    }

    internal void ClearStartupMetadataPin()
    {
        lock (_startupMetadataPinSync)
        {
            _pinnedStartupMetadataSnapshot = null;
        }
    }

    private Dictionary<string, byte[]?>? ConsumeStartupMetadataPin()
    {
        lock (_startupMetadataPinSync)
        {
            Dictionary<string, byte[]?>? pinned = _pinnedStartupMetadataSnapshot;
            _pinnedStartupMetadataSnapshot = null;
            return pinned;
        }
    }

    private async Task<HashSet<string>> WriteSnapshotMetadataFilesAsync(
        string snapshotDataDirectory,
        string backupKind,
        CancellationToken cancellationToken)
    {
        if (string.Equals(backupKind, "automatic", StringComparison.Ordinal) &&
            ConsumeStartupMetadataPin() is { } pinned)
        {
            var pinnedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string relativePath, byte[]? bytes) in pinned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pinnedSet.Add(relativePath);
                if (bytes is null)
                {
                    continue;
                }

                string destinationPath = Path.Combine(
                    snapshotDataDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
            }

            string[] unpinnedSafetyMetadata = s_fileSafetyMetadataNames
                .Where(name => !pinnedSet.Contains(name))
                .ToArray();
            if (unpinnedSafetyMetadata.Length > 0)
            {
                await CopyLockedMetadataFilesAsync(
                    snapshotDataDirectory,
                    unpinnedSafetyMetadata,
                    pinnedSet,
                    cancellationToken);
            }

            return pinnedSet;
        }

        var metadataSet = new HashSet<string>(s_fileSafetyMetadataNames, StringComparer.OrdinalIgnoreCase);
        await CopyLockedMetadataFilesAsync(
            snapshotDataDirectory,
            s_fileSafetyMetadataNames,
            metadataSet,
            cancellationToken);
        return metadataSet;
    }

    private async Task CopyLockedMetadataFilesAsync(
        string snapshotDataDirectory,
        IEnumerable<string> relativePaths,
        HashSet<string> trackedSet,
        CancellationToken cancellationToken)
    {
        await DesktopOrganizationTransaction.OperationGate.WaitAsync(cancellationToken);
        try
        {
            SemaphoreSlim settingsWriteLock = SettingsService.FileWriteLockFor(DataDirectory);
            await settingsWriteLock.WaitAsync(cancellationToken);
            try
            {
                foreach (string relativePath in relativePaths)
                {
                    trackedSet.Add(relativePath);
                    string sourcePath = Path.Combine(DataDirectory, relativePath);
                    if (!File.Exists(sourcePath))
                    {
                        continue;
                    }

                    await CopyStableSnapshotFileAsync(
                        sourcePath,
                        Path.Combine(snapshotDataDirectory, relativePath),
                        cancellationToken);
                }
            }
            finally
            {
                settingsWriteLock.Release();
            }
        }
        finally
        {
            DesktopOrganizationTransaction.OperationGate.Release();
        }
    }

    private static async Task<byte[]?> TryReadStableSnapshotBytesAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxSnapshotCopyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var before = new FileInfo(sourcePath);
                if (!before.Exists)
                {
                    return null;
                }

                long expectedLength = before.Length;
                DateTime expectedLastWriteUtc = before.LastWriteTimeUtc;
                byte[] buffer;
                await using (var source = new FileStream(
                                 sourcePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.ReadWrite | FileShare.Delete,
                                 bufferSize: 32768,
                                 useAsync: true))
                {
                    buffer = new byte[checked((int)expectedLength)];
                    int offset = 0;
                    while (offset < buffer.Length)
                    {
                        int read = await source.ReadAsync(
                            buffer.AsMemory(offset, buffer.Length - offset),
                            cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        offset += read;
                    }

                    if (offset != buffer.Length)
                    {
                        Array.Resize(ref buffer, offset);
                    }
                }

                var after = new FileInfo(sourcePath);
                if (after.Exists &&
                    buffer.LongLength == expectedLength &&
                    after.Length == expectedLength &&
                    after.LastWriteTimeUtc == expectedLastWriteUtc)
                {
                    return buffer;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == MaxSnapshotCopyAttempts)
                {
                    return null;
                }
            }

            if (attempt < MaxSnapshotCopyAttempts)
            {
                await Task.Yield();
            }
        }

        return null;
    }
}
