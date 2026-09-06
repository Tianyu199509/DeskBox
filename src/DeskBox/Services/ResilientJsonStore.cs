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
                App.Log($"[{logName}] Primary store is invalid: {ex}");
                QuarantineCorruptFile(storePath, logName);
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
        catch (Exception ex)
        {
            App.Log($"[{logName}] Backup store is invalid: {ex}");
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

    internal static Task SaveAsync(
        string storePath,
        string json,
        Action<string, string, string?, bool> replaceFile,
        Func<TimeSpan, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(json);
        return SaveCoreAsync(storePath, json, default, replaceFile, delayAsync);
    }

    internal static Task SaveAsync(
        string storePath,
        ReadOnlyMemory<byte> utf8Json,
        Action<string, string, string?, bool> replaceFile,
        Func<TimeSpan, Task> delayAsync) =>
        SaveCoreAsync(storePath, null, utf8Json, replaceFile, delayAsync);

    private static async Task SaveCoreAsync(
        string storePath,
        string? json,
        ReadOnlyMemory<byte> utf8Json,
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
            if (json is not null)
            {
                await File.WriteAllTextAsync(tempPath, json);
            }
            else
            {
                await File.WriteAllBytesAsync(tempPath, utf8Json);
            }
            if (File.Exists(storePath))
            {
                await ReplaceOrFallbackAsync(
                    tempPath,
                    storePath,
                    GetBackupPath(storePath),
                    replaceFile,
                    delayAsync);
            }
            else
            {
                File.Move(tempPath, storePath);
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
            catch (IOException ex) when (IsUnableToRemoveReplacedFile(ex))
            {
                if (retryIndex < s_replaceRetryDelays.Length)
                {
                    TimeSpan delay = s_replaceRetryDelays[retryIndex];
                    App.Log(
                        $"[ResilientJsonStore] Atomic replace could not remove the " +
                        $"destination; retrying in {delay.TotalMilliseconds:0} ms. " +
                        $"HResult=0x{ex.HResult:X8}, " +
                        $"File='{Path.GetFileName(storePath)}'.");
                    await delayAsync(delay);
                    continue;
                }

                // ERROR_UNABLE_TO_REMOVE_REPLACED documents that both source and
                // destination retain their original names. Guard that invariant
                // before using the non-atomic path so other partial replace errors
                // are never treated as safe fallbacks.
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

    private static bool IsUnableToRemoveReplacedFile(IOException exception) =>
        exception.HResult == UnableToRemoveReplacedFileHResult;

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
        }
        catch (Exception ex)
        {
            App.Log($"[{logName}] Failed to quarantine corrupt store: {ex}");
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
