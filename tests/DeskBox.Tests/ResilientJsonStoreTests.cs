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
    public async Task SaveAsync_OtherReplaceFailure_PropagatesWithoutChangingPrimary(bool useUtf8)
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        await File.WriteAllTextAsync(storePath, originalJson);
        var expected = new IOException("sharing violation", unchecked((int)0x80070020));

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
