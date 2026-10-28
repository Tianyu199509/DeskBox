using System.Text;
using System.Text.Json;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ResilientJsonStoreTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N")))
        .FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_UnableToRemoveReplacedFile_UsesVerifiedInPlaceFallback(bool useUtf8)
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        const string updatedJson = "{\"value\":\"更新 🌏\"}";
        await File.WriteAllTextAsync(storePath, originalJson);
        int replaceAttempts = 0;

        await SaveAsync(
            storePath,
            updatedJson,
            useUtf8,
            (_, _, _, _) =>
            {
                replaceAttempts++;
                throw CreateUnableToRemoveReplacedFileException();
            },
            _ => Task.CompletedTask);

        Assert.Equal(3, replaceAttempts);
        Assert.Equal(updatedJson, await File.ReadAllTextAsync(storePath));
        Assert.Equal(
            originalJson,
            await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_NonTransientReplaceFailure_PropagatesWithoutChangingPrimary(bool useUtf8)
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        await File.WriteAllTextAsync(storePath, originalJson);
        var expected = new IOException("disk full", unchecked((int)0x80070070));

        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            SaveAsync(
                storePath,
                "{\"value\":\"updated\"}",
                useUtf8,
                (_, _, _, _) => throw expected,
                _ => Task.CompletedTask));

        Assert.Same(expected, actual);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(storePath));
        Assert.False(File.Exists(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_SharingViolationTransientlyHeld_RecoversOnRetry(bool useUtf8)
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        const string updatedJson = "{\"value\":\"更新 🌏\"}";
        await File.WriteAllTextAsync(storePath, originalJson);
        int replaceAttempts = 0;

        await SaveAsync(
            storePath,
            updatedJson,
            useUtf8,
            (sourcePath, destinationPath, backupPath, ignoreMetadataErrors) =>
            {
                replaceAttempts++;
                if (replaceAttempts <= 2)
                {
                    throw new IOException(
                        "sharing violation",
                        ResilientJsonStore.SharingViolationHResult);
                }

                File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors);
            },
            _ => Task.CompletedTask);

        Assert.Equal(3, replaceAttempts);
        Assert.Equal(updatedJson, await File.ReadAllTextAsync(storePath));
        Assert.Equal(
            originalJson,
            await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_SharingViolationHeldTooLong_UsesVerifiedInPlaceFallback(bool useUtf8)
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        const string updatedJson = "{\"value\":\"更新 🌏\"}";
        await File.WriteAllTextAsync(storePath, originalJson);
        int replaceAttempts = 0;

        await SaveAsync(
            storePath,
            updatedJson,
            useUtf8,
            (_, _, _, _) =>
            {
                replaceAttempts++;
                throw new IOException(
                    "sharing violation",
                    ResilientJsonStore.SharingViolationHResult);
            },
            _ => Task.CompletedTask);

        // 1 initial attempt + both retry delays, then the guarded fallback.
        Assert.Equal(3, replaceAttempts);
        Assert.Equal(updatedJson, await File.ReadAllTextAsync(storePath));
        Assert.Equal(
            originalJson,
            await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_SharingViolationAfterPartialReplace_DoesNotUseFallback(bool useUtf8)
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        await File.WriteAllTextAsync(storePath, originalJson);

        // A replace stopped mid-way can leave the primary renamed away; the
        // existence guard must fail closed instead of trusting the fallback.
        await Assert.ThrowsAsync<IOException>(() =>
            SaveAsync(
                storePath,
                "{\"value\":\"updated\"}",
                useUtf8,
                (_, destinationPath, _, _) =>
                {
                    File.Delete(destinationPath);
                    throw new IOException(
                        "sharing violation",
                        ResilientJsonStore.SharingViolationHResult);
                },
                _ => Task.CompletedTask));

        Assert.False(File.Exists(storePath));
        Assert.False(File.Exists(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Fact]
    public async Task LoadAsync_PrimaryLockedByExternalHandle_KeepsPrimaryAndUsesBackup()
    {
        string storePath = Path.Combine(_tempRoot, "locked-primary.json");
        const string firstJson = "{\"value\":\"第一次 📁\"}";
        const string secondJson = "{\"value\":\"第二次 🌏\"}";
        await File.WriteAllTextAsync(storePath, firstJson);
        await File.WriteAllTextAsync(ResilientJsonStore.GetBackupPath(storePath), secondJson);

        await using (new FileStream(
            storePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            var result = await ResilientJsonStore.LoadWithResultAsync(
                storePath,
                json =>
                {
                    using var document = JsonDocument.Parse(json);
                    return document.RootElement.GetProperty("value").GetString()!;
                },
                () => "default",
                "ResilientJsonStoreTests");

            Assert.Equal(ResilientJsonLoadSource.Backup, result.Source);
            Assert.Equal("第二次 🌏", result.Value);
            Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.corrupt-*"));
        }

        // A locked-but-valid primary must not be quarantined, and the backup
        // must not be restored over it: both would silently roll the store
        // back once the external handle goes away. The next load converges
        // on the primary bytes.
        Assert.Equal(firstJson, await File.ReadAllTextAsync(storePath));
        Assert.Equal(
            secondJson,
            await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
        var converged = await ResilientJsonStore.LoadWithResultAsync(
            storePath,
            json =>
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.GetProperty("value").GetString()!;
            },
            () => "default",
            "ResilientJsonStoreTests");
        Assert.Equal(ResilientJsonLoadSource.Primary, converged.Source);
        Assert.Equal("第一次 📁", converged.Value);
    }

    [Fact]
    public async Task LoadAsync_BackupLockedByExternalHandle_KeepsBackupAndFallsBackToDefault()
    {
        string storePath = Path.Combine(_tempRoot, "locked-backup.json");
        const string backupJson = "{\"value\":\"备份 📁\"}";
        await File.WriteAllTextAsync(storePath, "{invalid");
        string backupPath = ResilientJsonStore.GetBackupPath(storePath);
        await File.WriteAllTextAsync(backupPath, backupJson);

        await using (new FileStream(
            backupPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            var result = await ResilientJsonStore.LoadWithResultAsync(
                storePath,
                json =>
                {
                    using var document = JsonDocument.Parse(json);
                    return document.RootElement.GetProperty("value").GetString()!;
                },
                () => "default",
                "ResilientJsonStoreTests");

            Assert.Equal(ResilientJsonLoadSource.DefaultAfterFailure, result.Source);
            Assert.Equal("default", result.Value);
            // The genuinely corrupt primary is still quarantined; the merely
            // locked backup is not — it keeps its bytes for the next load.
            Assert.Single(Directory.EnumerateFiles(_tempRoot, "*.corrupt-*"));
            Assert.False(File.Exists(storePath));
        }

        Assert.Equal(backupJson, await File.ReadAllTextAsync(backupPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_UnableToRemoveAfterPartialReplace_DoesNotUseFallback(bool useUtf8)
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        await File.WriteAllTextAsync(storePath, originalJson);

        await Assert.ThrowsAsync<IOException>(() =>
            SaveAsync(
                storePath,
                "{\"value\":\"updated\"}",
                useUtf8,
                (sourcePath, _, _, _) =>
                {
                    File.Delete(sourcePath);
                    throw CreateUnableToRemoveReplacedFileException();
                },
                _ => Task.CompletedTask));

        Assert.Equal(originalJson, await File.ReadAllTextAsync(storePath));
        Assert.False(File.Exists(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_NormalReplace_PreservesPreviousVersionAsBackup(bool useUtf8)
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"原始 🌏\"}";
        const string updatedJson = "{\"value\":\"更新 📁\"}";
        await File.WriteAllTextAsync(storePath, originalJson);

        await SaveAsync(storePath, updatedJson, useUtf8);

        Assert.Equal(updatedJson, await File.ReadAllTextAsync(storePath));
        Assert.Equal(
            originalJson,
            await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Equal(Encoding.UTF8.GetBytes(updatedJson), await File.ReadAllBytesAsync(storePath));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_FirstSave_PreservesUnicodeWithoutBom(bool useUtf8)
    {
        string storePath = Path.Combine(_tempRoot, "nested", "settings.json");
        const string json = "{\"value\":\"中文、é、📁和\\n换行\"}";

        await SaveAsync(storePath, json, useUtf8);

        Assert.Equal(Encoding.UTF8.GetBytes(json), await File.ReadAllBytesAsync(storePath));
        Assert.False(File.Exists(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_BackupCanRecoverCorruptPrimary(bool useUtf8)
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"已保存 📁\"}";
        await SaveAsync(storePath, originalJson, useUtf8);
        await SaveAsync(storePath, "{\"value\":\"new\"}", useUtf8);
        await File.WriteAllTextAsync(storePath, "{invalid");

        var result = await ResilientJsonStore.LoadWithResultAsync(
            storePath,
            json =>
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.GetProperty("value").GetString()!;
            },
            () => "default",
            "ResilientJsonStoreTests");

        Assert.Equal(ResilientJsonLoadSource.Backup, result.Source);
        Assert.Equal("已保存 📁", result.Value);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(storePath));
        Assert.Equal(originalJson, await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Single(Directory.EnumerateFiles(_tempRoot, "*.corrupt-*"));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_StreamedTemp_RoundTripsAndPreservesPreviousVersionAsBackup()
    {
        string storePath = Path.Combine(_tempRoot, "streamed.json");
        const string originalJson = "{\"value\":\"原始 📁\"}";
        const string updatedJson = "{\"value\":\"更新 🌏\"}";
        await File.WriteAllTextAsync(storePath, originalJson);

        await ResilientJsonStore.SaveAsync(
            storePath,
            tempPath => File.WriteAllTextAsync(tempPath, updatedJson));

        Assert.Equal(updatedJson, await File.ReadAllTextAsync(storePath));
        Assert.Equal(
            originalJson,
            await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_StreamedTemp_FirstSaveMovesIntoPlace()
    {
        string storePath = Path.Combine(_tempRoot, "nested", "streamed-first.json");
        const string json = "{\"value\":\"中文、é、📁\"}";

        await ResilientJsonStore.SaveAsync(
            storePath,
            tempPath => File.WriteAllTextAsync(tempPath, json));

        Assert.Equal(json, await File.ReadAllTextAsync(storePath));
        Assert.False(File.Exists(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SaveAsync_StreamedTemp_WriterThrows_PropagatesAndKeepsOldSettingsRecoverable()
    {
        string storePath = Path.Combine(_tempRoot, "streamed-fail.json");
        const string originalJson = "{\"value\":\"original\"}";
        await File.WriteAllTextAsync(storePath, originalJson);

        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            ResilientJsonStore.SaveAsync(
                storePath,
                _ => throw new IOException("disk full while encoding")));

        Assert.Equal("disk full while encoding", actual.Message);
        // The old settings survive untouched: same primary, no backup
        // change, and no temp residue - a failed streamed save leaves the
        // store exactly as it was.
        Assert.Equal(originalJson, await File.ReadAllTextAsync(storePath));
        Assert.False(File.Exists(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_StreamedTemp_UnableToRemoveReplacedFile_UsesVerifiedInPlaceFallback()
    {
        string storePath = Path.Combine(_tempRoot, "streamed-1175.json");
        const string originalJson = "{\"value\":\"original\"}";
        const string updatedJson = "{\"value\":\"更新 🌏\"}";
        await File.WriteAllTextAsync(storePath, originalJson);
        int replaceAttempts = 0;

        await ResilientJsonStore.SaveAsync(
            storePath,
            tempPath => File.WriteAllTextAsync(tempPath, updatedJson),
            (_, _, _, _) =>
            {
                replaceAttempts++;
                throw CreateUnableToRemoveReplacedFileException();
            },
            _ => Task.CompletedTask);

        // The exceptional in-place path must re-read the streamed temp file:
        // with the buffer overloads it read the pending bytes directly.
        Assert.Equal(3, replaceAttempts);
        Assert.Equal(updatedJson, await File.ReadAllTextAsync(storePath));
        Assert.Equal(
            originalJson,
            await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_StreamedTemp_BackupCanRecoverCorruptPrimary()
    {
        string storePath = Path.Combine(_tempRoot, "streamed-recovery.json");
        const string firstJson = "{\"value\":\"第一次 📁\"}";
        const string secondJson = "{\"value\":\"第二次 🌏\"}";
        await ResilientJsonStore.SaveAsync(
            storePath,
            tempPath => File.WriteAllTextAsync(tempPath, firstJson));
        await ResilientJsonStore.SaveAsync(
            storePath,
            tempPath => File.WriteAllTextAsync(tempPath, secondJson));
        await File.WriteAllTextAsync(storePath, "{invalid");

        var result = await ResilientJsonStore.LoadWithResultAsync(
            storePath,
            json =>
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.GetProperty("value").GetString()!;
            },
            () => "default",
            "ResilientJsonStoreTests");

        Assert.Equal(ResilientJsonLoadSource.Backup, result.Source);
        Assert.Equal("第一次 📁", result.Value);
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static IOException CreateUnableToRemoveReplacedFileException() =>
        new(
            "unable to remove replaced file",
            ResilientJsonStore.UnableToRemoveReplacedFileHResult);

    private static Task SaveAsync(string storePath, string json, bool useUtf8) =>
        useUtf8
            ? ResilientJsonStore.SaveAsync(storePath, Encoding.UTF8.GetBytes(json))
            : ResilientJsonStore.SaveAsync(storePath, json);

    private static Task SaveAsync(
        string storePath,
        string json,
        bool useUtf8,
        Action<string, string, string?, bool> replaceFile,
        Func<TimeSpan, Task> delayAsync) =>
        useUtf8
            ? ResilientJsonStore.SaveAsync(storePath, Encoding.UTF8.GetBytes(json), replaceFile, delayAsync)
            : ResilientJsonStore.SaveAsync(storePath, json, replaceFile, delayAsync);
}
