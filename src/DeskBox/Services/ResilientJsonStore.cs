namespace DeskBox.Services;

internal enum ResilientJsonLoadSource
{
    Primary,
    Backup,
    DefaultMissing,
    DefaultAfterFailure
}

internal sealed record ResilientJsonLoadResult<T>(
    T Value,
    ResilientJsonLoadSource Source);

internal static class ResilientJsonStore
{
    // Win32 ERROR_UNABLE_TO_REMOVE_REPLACED (1175), surfaced by File.Replace.
    // This has been observed for packaged Store data files even though the same
    // files remain readable and writable through ordinary file handles.
    internal const int UnableToRemoveReplacedFileHResult = unchecked((int)0x80070497);

    // Win32 ERROR_SHARING_VIOLATION (32): some other handle holds the target
    // — an antivirus/indexer/sync scan, or a racing writer in another
    // process. Same class of transient commit conflict as the 1175 above,
    // but it used to propagate uncaught (stowed-exception fail-fast,
    // 2026-10-26) instead of entering the retry ladder.
    internal const int SharingViolationHResult = unchecked((int)0x80070020);

    private static readonly TimeSpan[] s_replaceRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150)
    ];

    internal static string GetBackupPath(string storePath) => $"{storePath}.bak";

    public static async Task<T> LoadAsync<T>(
        string storePath,
        Func<string, T> deserialize,
        Func<T> createDefault,
        string logName)
    {
        ResilientJsonLoadResult<T> result = await LoadWithResultAsync(
            storePath,
            deserialize,
            createDefault,
            logName);
        return result.Value;
    }

    public static async Task<ResilientJsonLoadResult<T>> LoadWithResultAsync<T>(
        string storePath,
        Func<string, T> deserialize,
        Func<T> createDefault,
        string logName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentNullException.ThrowIfNull(createDefault);

        bool primaryFailed = false;
        bool primaryUnreadable = false;
        if (File.Exists(storePath))
        {
            try
            {
                return new ResilientJsonLoadResult<T>(
                    deserialize(await File.ReadAllTextAsync(storePath)),
                    ResilientJsonLoadSource.Primary);
            }
            catch (Exception ex)
            {
                primaryFailed = true;
                if (ex is IOException or UnauthorizedAccessException)
                {
                    // An external handle (antivirus/indexer/sync scan) or an
                    // ACL denial fails the read without corrupting the bytes.
                    // Quarantining here would rotate a perfectly good primary
                    // to .corrupt-* and silently fall the store back to an
                    // older .bak; keep the primary so the next load converges
                    // on it once the handle is gone.
                    primaryUnreadable = true;
                    App.Log($"[{logName}] Primary store could not be read: {ex}");
                }
                else
                {
                    App.Log($"[{logName}] Primary store is invalid: {ex}");
                    QuarantineCorruptFile(storePath, logName);
                }
            }
        }

        string backupPath = GetBackupPath(storePath);
        if (!File.Exists(backupPath))
        {
            return new ResilientJsonLoadResult<T>(
                createDefault(),
                primaryFailed
                    ? ResilientJsonLoadSource.DefaultAfterFailure
                    : ResilientJsonLoadSource.DefaultMissing);
        }

        T recovered;
        try
        {
            string backupJson = await File.ReadAllTextAsync(backupPath);
            recovered = deserialize(backupJson);

            if (primaryUnreadable)
            {
                // The primary never proved corrupt — restoring the older
                // backup bytes over it would be a silent rollback of newer
                // committed state once the transient handle goes away.
                App.Log(
                    $"[{logName}] Backup loaded; primary kept for the next " +
                    $"read after its transient read failure.");
            }
            else
            {
                try
                {
                    await RestorePrimaryAsync(storePath, backupJson);
                    App.Log($"[{logName}] Restored store from '{backupPath}'.");
                }
                catch (Exception restoreException)
                {
                    // A read-only or temporarily locked data directory must not
                    // turn a valid backup into an apparent total settings loss.
                    App.Log(
                        $"[{logName}] Backup loaded, but primary restore failed: " +
                        restoreException);
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is IOException or UnauthorizedAccessException)
            {
                // Symmetric to the primary path: an unreadable backup keeps
                // its bytes for the next load.
                App.Log($"[{logName}] Backup store could not be read: {ex}");
            }
            else
            {
                App.Log($"[{logName}] Backup store is invalid: {ex}");
                // Quarantine symmetric to the primary path: a corrupt .bak left
                // in place keeps "backup exists" probes true forever while every
                // load still fails — callers checking File.Exists(backup) would
                // deadlock on a recovery source that can never succeed.
                QuarantineCorruptFile(backupPath, logName);
            }

            return new ResilientJsonLoadResult<T>(
                createDefault(),
                ResilientJsonLoadSource.DefaultAfterFailure);
        }

        return new ResilientJsonLoadResult<T>(
            recovered,
            ResilientJsonLoadSource.Backup);
    }

    public static Task SaveAsync(string storePath, string json)
    {
        return SaveAsync(
            storePath,
            json,
            static (sourcePath, destinationPath, backupPath, ignoreMetadataErrors) =>
                File.Replace(
                    sourcePath,
                    destinationPath,
                    backupPath,
                    ignoreMetadataErrors),
            Task.Delay);
    }

    public static Task SaveAsync(string storePath, ReadOnlyMemory<byte> utf8Json)
    {
        return SaveAsync(
            storePath,
            utf8Json,
            static (sourcePath, destinationPath, backupPath, ignoreMetadataErrors) =>
                File.Replace(
                    sourcePath,
                    destinationPath,
                    backupPath,
                    ignoreMetadataErrors),
            Task.Delay);
    }

    /// <summary>
    /// Saves a store whose payload is produced directly by
    /// <paramref name="writeTempAsync"/> into the store's temp file. The
    /// store still owns the whole commit protocol - unique temp name,
    /// atomic replace, backup, the verified in-place fallback, and temp
    /// cleanup - exactly as for the buffered overloads. Callers that would
    /// otherwise materialize a multi-megabyte byte[] (full settings
    /// serialization) stream into the temp file instead and never allocate
    /// the buffer.
    /// </summary>
    public static Task SaveAsync(string storePath, Func<string, Task> writeTempAsync)
    {
        return SaveAsync(
            storePath,
            writeTempAsync,
            static (sourcePath, destinationPath, backupPath, ignoreMetadataErrors) =>
                File.Replace(
                    sourcePath,
                    destinationPath,
                    backupPath,
                    ignoreMetadataErrors),
            Task.Delay);
    }

    internal static Task SaveAsync(
        string storePath,
        string json,
        Action<string, string, string?, bool> replaceFile,
        Func<TimeSpan, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(json);
        return SaveCoreAsync(storePath, json, default, writeTempAsync: null, replaceFile, delayAsync);
    }

    internal static Task SaveAsync(
        string storePath,
        ReadOnlyMemory<byte> utf8Json,
        Action<string, string, string?, bool> replaceFile,
        Func<TimeSpan, Task> delayAsync) =>
        SaveCoreAsync(storePath, null, utf8Json, writeTempAsync: null, replaceFile, delayAsync);

    internal static Task SaveAsync(
        string storePath,
        Func<string, Task> writeTempAsync,
        Action<string, string, string?, bool> replaceFile,
        Func<TimeSpan, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(writeTempAsync);
        return SaveCoreAsync(storePath, null, default, writeTempAsync, replaceFile, delayAsync);
    }

    private static async Task SaveCoreAsync(
        string storePath,
        string? json,
        ReadOnlyMemory<byte> utf8Json,
        Func<string, Task>? writeTempAsync,
        Action<string, string, string?, bool> replaceFile,
        Func<TimeSpan, Task> delayAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(replaceFile);
        ArgumentNullException.ThrowIfNull(delayAsync);

        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        string tempPath = $"{storePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (writeTempAsync is not null)
            {
                await writeTempAsync(tempPath);
            }
            else if (json is not null)
            {
                await File.WriteAllTextAsync(tempPath, json);
            }
            else
            {
                await File.WriteAllBytesAsync(tempPath, utf8Json);
            }

            // The existence probe routes the commit: replace when a primary
            // exists, plain move for the first save. A racing writer can
            // flip the answer between the probe and the move, so a failed
            // initial move loops back through the probe (attempt 2 then
            // sees the racing writer's primary and replaces it).
            for (int commitAttempt = 0; ; commitAttempt++)
            {
                if (File.Exists(storePath))
                {
                    await ReplaceOrFallbackAsync(
                        tempPath,
                        storePath,
                        GetBackupPath(storePath),
                        replaceFile,
                        delayAsync);
                    return;
                }

                try
                {
                    File.Move(tempPath, storePath);
                    return;
                }
                catch (IOException ex) when (commitAttempt < s_replaceRetryDelays.Length)
                {
                    // Initial-move failures at this point are races or
                    // transient locks (the temp write already succeeded, so
                    // media-level errors have already surfaced there).
                    App.Log(
                        $"[ResilientJsonStore] Initial move hit a transient " +
                        $"conflict; retrying commit. HResult=0x{ex.HResult:X8}, " +
                        $"File='{Path.GetFileName(storePath)}'.");
                    await delayAsync(s_replaceRetryDelays[commitAttempt]);
                }
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static async Task ReplaceOrFallbackAsync(
        string tempPath,
        string storePath,
        string backupPath,
        Action<string, string, string?, bool> replaceFile,
        Func<TimeSpan, Task> delayAsync)
    {
        for (int retryIndex = 0; ; retryIndex++)
        {
            try
            {
                replaceFile(
                    tempPath,
                    storePath,
                    backupPath,
                    true);
                return;
            }
            catch (IOException ex) when (IsTransientReplaceConflict(ex))
            {
                if (retryIndex < s_replaceRetryDelays.Length)
                {
                    TimeSpan delay = s_replaceRetryDelays[retryIndex];
                    App.Log(
                        $"[ResilientJsonStore] Atomic replace hit a transient " +
                        $"conflict; retrying in {delay.TotalMilliseconds:0} ms. " +
                        $"HResult=0x{ex.HResult:X8}, " +
                        $"File='{Path.GetFileName(storePath)}'.");
                    await delayAsync(delay);
                    continue;
                }

                // The in-place fallback is only safe when the failed replace
                // left BOTH files under their original names. For
                // ERROR_UNABLE_TO_REMOVE_REPLACED that is documented; for a
                // sharing violation it is not (the replace can stop in any
                // intermediate rename step), so this existence probe is the
                // only guard standing between a partial replace and the
                // non-atomic path — it must fail closed.
                if (!File.Exists(tempPath) || !File.Exists(storePath))
                {
                    throw;
                }

                App.Log(
                    $"[ResilientJsonStore] Atomic replace remained unavailable; " +
                    $"using verified-backup in-place fallback. " +
                    $"HResult=0x{ex.HResult:X8}, " +
                    $"File='{Path.GetFileName(storePath)}'.");
                await SaveInPlaceWithVerifiedBackupAsync(
                    storePath,
                    backupPath,
                    tempPath);
                return;
            }
        }
    }

    /// <summary>
    /// Undoes a completed commit so a composite save (layout + settings)
    /// can leave no half-persisted pair. When the commit replaced an
    /// existing primary, that save rotated the pre-commit bytes into
    /// .bak — restore them. When the commit created the primary fresh,
    /// pre-commit state is the file's absence — delete it.
    /// </summary>
    internal static void RevertLastCommit(
        string storePath,
        bool primaryExistedBeforeCommit)
    {
        if (primaryExistedBeforeCommit)
        {
            File.Copy(GetBackupPath(storePath), storePath, overwrite: true);
        }
        else
        {
            File.Delete(storePath);
        }
    }

    private static bool IsUnableToRemoveReplacedFile(IOException exception) =>
        exception.HResult == UnableToRemoveReplacedFileHResult;

    private static bool IsTransientReplaceConflict(IOException exception) =>
        exception.HResult == UnableToRemoveReplacedFileHResult ||
        exception.HResult == SharingViolationHResult;

    private static async Task SaveInPlaceWithVerifiedBackupAsync(
        string storePath,
        string backupPath,
        string tempPath)
    {
        // Use the already encoded pending file for either caller. This path is
        // exceptional; normal saves do not read or copy the UTF-8 payload again.
        byte[] updatedBytes = await File.ReadAllBytesAsync(tempPath);
        byte[] originalBytes = await File.ReadAllBytesAsync(storePath);
        await WriteAllBytesInPlaceAsync(backupPath, originalBytes);
        await VerifyFileContentsAsync(backupPath, originalBytes, "backup");

        await WriteAllBytesInPlaceAsync(storePath, updatedBytes);
        await VerifyFileContentsAsync(storePath, updatedBytes, "primary store");
    }

    private static async Task WriteAllBytesInPlaceAsync(
        string path,
        ReadOnlyMemory<byte> contents)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
    }

    private static async Task VerifyFileContentsAsync(
        string path,
        ReadOnlyMemory<byte> expected,
        string description)
    {
        byte[] actual = await File.ReadAllBytesAsync(path);
        if (!actual.AsSpan().SequenceEqual(expected.Span))
        {
            throw new IOException(
                $"The {description} could not be verified after the in-place save.");
        }
    }

    private static void QuarantineCorruptFile(string storePath, string logName)
    {
        string corruptPath = $"{storePath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        try
        {
            File.Move(storePath, corruptPath);
            App.Log($"[{logName}] Preserved corrupt store as '{corruptPath}'.");
            PruneQuarantinedCopies(storePath, logName);
        }
        catch (Exception ex)
        {
            App.Log($"[{logName}] Failed to quarantine corrupt store: {ex}");
        }
    }

    /// <summary>
    /// Quarantined copies are recent forensic evidence, not an archive: keep
    /// only the newest two per quarantined file. Nothing pruned them before,
    /// and a store flapping under concurrent writers (test hosts racing the
    /// running app) once accumulated dozens of copies.
    /// </summary>
    private const int MaxQuarantinedCopiesPerStore = 2;

    private static void PruneQuarantinedCopies(string quarantinedPath, string logName)
    {
        try
        {
            string? directory = Path.GetDirectoryName(quarantinedPath);
            if (directory is null)
            {
                return;
            }

            string prefix = $"{Path.GetFileName(quarantinedPath)}.corrupt-";
            foreach (string stale in Directory.EnumerateFiles(directory, prefix + "*")
                .OrderDescending(StringComparer.Ordinal)
                .Skip(MaxQuarantinedCopiesPerStore))
            {
                TryDeleteFile(stale);
                App.Log(
                    $"[{logName}] Pruned old quarantined copy " +
                    $"'{Path.GetFileName(stale)}'.");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[{logName}] Quarantine prune failed: {ex.Message}");
        }
    }

    private static async Task RestorePrimaryAsync(string storePath, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        string tempPath = $"{storePath}.{Guid.NewGuid():N}.recovery.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, storePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
