using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class PersistenceBackupAndIpcHardeningTests : IDisposable
{
    private readonly string _tempRoot;

    public PersistenceBackupAndIpcHardeningTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task ResilientJsonStore_SaveAsync_RecoversFromConcurrentFirstSaveDestinationRace()
    {
        string storeDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "store-race")).FullName;
        string storePath = Path.Combine(storeDir, "state.json");

        // Pre-create an empty directory at storePath so File.Exists(storePath) is false on
        // commitAttempt 0 while File.Move(tempPath, storePath) deterministically throws IOException.
        // During the first retry delay, simulate a concurrent writer finishing its primary file creation.
        Directory.CreateDirectory(storePath);
        int retryDelayCalls = 0;

        await ResilientJsonStore.SaveAsync(
            storePath,
            "{\"revision\":2}",
            static (source, destination, backup, ignoreMetadataErrors) =>
                File.Replace(source, destination, backup, ignoreMetadataErrors),
            _ =>
            {
                retryDelayCalls++;
                if (Directory.Exists(storePath))
                {
                    Directory.Delete(storePath);
                    File.WriteAllText(storePath, "{\"revision\":1}");
                }

                return Task.CompletedTask;
            });

        Assert.Equal(1, retryDelayCalls);
        Assert.Equal("{\"revision\":2}", await File.ReadAllTextAsync(storePath));
        Assert.Equal("{\"revision\":1}", await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
    }

    [Fact]
    public async Task ResilientJsonStore_LoadUtf8WithResultAsync_StripsUtf8BomAndReadsPrimary()
    {
        string storePath = Path.Combine(_tempRoot, "bom-store.json");
        byte[] payload = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"count\":7}")];
        await File.WriteAllBytesAsync(storePath, payload);

        ResilientJsonLoadResult<int> loaded = await ResilientJsonStore.LoadUtf8WithResultAsync(
            storePath,
            utf8 =>
            {
                Assert.NotEqual((byte)0xEF, utf8.Span[0]);
                using JsonDocument doc = JsonDocument.Parse(utf8);
                return doc.RootElement.GetProperty("count").GetInt32();
            },
            static () => -1,
            "BomTest");

        Assert.Equal(ResilientJsonLoadSource.Primary, loaded.Source);
        Assert.Equal(7, loaded.Value);
        Assert.Empty(Directory.GetFiles(_tempRoot, "bom-store.json.corrupt-*"));
    }

    [Fact]
    public async Task ResilientJsonStore_LoadWithResultAsync_UnauthorizedAccessDoesNotQuarantinePrimary()
    {
        string storeDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "acl-store")).FullName;
        string storePath = Path.Combine(storeDir, "settings.json");
        string backupPath = ResilientJsonStore.GetBackupPath(storePath);
        await File.WriteAllTextAsync(storePath, "{\"generation\":2}");
        await File.WriteAllTextAsync(backupPath, "{\"generation\":1}");

        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier userSid = Assert.IsType<SecurityIdentifier>(identity.User);
        var fileInfo = new FileInfo(storePath);
        FileSecurity originalSecurity = fileInfo.GetAccessControl();
        var denyReadRule = new FileSystemAccessRule(
            userSid,
            FileSystemRights.ReadData,
            AccessControlType.Deny);

        FileSecurity restricted = fileInfo.GetAccessControl();
        try
        {
            restricted.AddAccessRule(denyReadRule);
            fileInfo.SetAccessControl(restricted);

            ResilientJsonLoadResult<int> fallbackResult = await ResilientJsonStore.LoadWithResultAsync(
                storePath,
                static json => JsonDocument.Parse(json).RootElement.GetProperty("generation").GetInt32(),
                static () => 0,
                "AclDeniedTest");

            Assert.Equal(ResilientJsonLoadSource.Backup, fallbackResult.Source);
            Assert.Equal(1, fallbackResult.Value);
            Assert.True(File.Exists(storePath));
            Assert.Empty(Directory.GetFiles(storeDir, "settings.json.corrupt-*"));
        }
        finally
        {
            restricted.RemoveAccessRuleSpecific(denyReadRule);
            fileInfo.SetAccessControl(restricted);
        }

        // Once ACL access is restored, the newer primary file must still be intact (not overwritten by .bak).
        Assert.Equal("{\"generation\":2}", await File.ReadAllTextAsync(storePath));
    }

    [Fact]
    public async Task ResilientJsonStore_QuarantineCorruptFile_PrunesOldestAndRetainsNewestTwo()
    {
        string storeDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "quarantine-prune")).FullName;
        string storePath = Path.Combine(storeDir, "corruptible.json");
        string oldestManual = $"{storePath}.corrupt-20200101-000001-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string middleManual = $"{storePath}.corrupt-20250101-000001-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        await File.WriteAllTextAsync(oldestManual, "old-corrupt");
        await File.WriteAllTextAsync(middleManual, "middle-corrupt");
        await File.WriteAllTextAsync(storePath, "{ invalid-json-payload");

        ResilientJsonLoadResult<int> result = await ResilientJsonStore.LoadWithResultAsync(
            storePath,
            static json => JsonDocument.Parse(json).RootElement.GetProperty("v").GetInt32(),
            static () => 99,
            "PruneTest");

        Assert.Equal(ResilientJsonLoadSource.DefaultAfterFailure, result.Source);
        Assert.Equal(99, result.Value);

        string[] remaining = Directory.GetFiles(storeDir, "corruptible.json.corrupt-*")
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, remaining.Length);
        Assert.False(File.Exists(oldestManual));
        Assert.Contains(middleManual, remaining);
    }

    [Fact]
    public async Task SettingsService_CompositeSaveFailureOnSettings_RollsBackCommittedWidgetLayout()
    {
        string settingsDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "composite-rollback")).FullName;
        var service = new SettingsService(settingsDir);
        await service.LoadAsync();

        service.Settings.WidgetLayout.Widgets.Add(new WidgetConfig
        {
            Id = "initial-widget",
            Name = "Initial",
            WidgetKind = WidgetKind.Todo
        });
        Assert.True(await service.SaveCheckedAsync(notifySubscribers: false));

        string layoutPath = Path.Combine(settingsDir, "widget-layout.json");
        string settingsPath = Path.Combine(settingsDir, "settings.json");
        string preFailureLayoutJson = await File.ReadAllTextAsync(layoutPath);

        // Block settings.json commit by replacing it with a directory while allowing widget-layout.json to commit first.
        File.Delete(settingsPath);
        Directory.CreateDirectory(settingsPath);

        service.Settings.WidgetLayout.Widgets.Add(new WidgetConfig
        {
            Id = "uncommitted-widget",
            Name = "ShouldRollback",
            WidgetKind = WidgetKind.Todo
        });
        service.Settings.GlobalHotkeyEnabled = !service.Settings.GlobalHotkeyEnabled;

        bool saved = await service.SaveCheckedAsync(notifySubscribers: false);

        Assert.False(saved);
        string rolledBackLayoutJson = await File.ReadAllTextAsync(layoutPath);
        Assert.Equal(preFailureLayoutJson, rolledBackLayoutJson);
        Assert.DoesNotContain("uncommitted-widget", rolledBackLayoutJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsService_TryReloadFromDiskAsync_PreservesInMemoryWidgetLayoutAndFeatureStates()
    {
        string writerDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "reload-writer")).FullName;
        var writer = new SettingsService(writerDir);
        await writer.LoadAsync();
        writer.Settings.Language = "en-US";
        Assert.True(await writer.SaveCheckedAsync(notifySubscribers: false));

        string hostDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "reload-host")).FullName;
        File.Copy(Path.Combine(writerDir, "settings.json"), Path.Combine(hostDir, "settings.json"));

        var host = new SettingsService(hostDir);
        await host.LoadAsync();
        host.Settings.WidgetLayout.Widgets.Add(new WidgetConfig
        {
            Id = "live-host-widget",
            Name = "LiveWidget",
            WidgetKind = WidgetKind.Todo
        });
        host.Settings.WidgetLayout.FeatureWidgetEnabledStates["Clipboard"] = true;

        writer.Settings.Language = "ja-JP";
        Assert.True(await writer.SaveCheckedAsync(notifySubscribers: false));
        File.Copy(Path.Combine(writerDir, "settings.json"), Path.Combine(hostDir, "settings.json"), overwrite: true);

        Assert.True(await host.TryReloadFromDiskAsync(notifySubscribers: false));
        Assert.Equal("ja-JP", host.Settings.Language);
        Assert.Contains(host.Settings.WidgetLayout.Widgets, static w => w.Id == "live-host-widget");
        Assert.True(host.Settings.WidgetLayout.FeatureWidgetEnabledStates.TryGetValue("Clipboard", out bool enabled) && enabled);
    }

    [Fact]
    public async Task DeskBoxDataBackupService_ApplyPendingRestoreAsync_RollsBackLiveDataWhenStagedMoveFails()
    {
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "restore-src")).FullName;
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sourceData, "settings.json"), "{\"language\":\"en-US\"}");
        string exportRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "restore-export")).FullName;
        string archivePath = await new DeskBoxDataBackupService(sourceRoot).ExportBackupAsync(exportRoot);

        string appRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "restore-target")).FullName;
        string liveData = Directory.CreateDirectory(Path.Combine(appRoot, "data")).FullName;
        string liveSettingsPath = Path.Combine(liveData, "settings.json");
        await File.WriteAllTextAsync(liveSettingsPath, "{\"language\":\"zh-CN\"}");

        var service = new DeskBoxDataBackupService(appRoot);
        await service.PrepareRestoreAsync(archivePath);
        Assert.True(await service.ConfirmPendingFullRestoreAsync());

        using JsonDocument marker = JsonDocument.Parse(await File.ReadAllTextAsync(service.PendingRestoreMarkerPath));
        string stagingRoot = marker.RootElement.GetProperty("stagingRoot").GetString()!;
        string stagedSettingsPath = Path.Combine(stagingRoot, "data", "settings.json");

        // Hold an open handle with FileShare.ReadWrite so ValidateRestoreData succeeds,
        // while Directory.Move(stagedDataDirectory, DataDirectory) fails after DataDirectory was moved aside.
        DeskBoxRestoreApplyResult applyResult;
        using (var openStagedHandle = new FileStream(
                   stagedSettingsPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite))
        {
            applyResult = await service.ApplyPendingRestoreAsync();
        }

        Assert.True(applyResult.HadPendingRestore);
        Assert.False(applyResult.Succeeded);
        Assert.True(File.Exists(liveSettingsPath));
        Assert.Contains("zh-CN", await File.ReadAllTextAsync(liveSettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeskBoxDataBackupService_ScopedRestore_RejectsOutOfScopeSmuggledSettingsEntry()
    {
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "scoped-src")).FullName;
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "todo-1");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "t1", Text = "legit task" }]
        });

        string exportRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "scoped-export")).FullName;
        string archivePath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(exportRoot, CloudBackupDomain.TodoData);

        // Tamper with the scoped ZIP by injecting an out-of-scope data/settings.json entry.
        using (ZipArchive zip = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            ZipArchiveEntry smuggled = zip.CreateEntry("data/settings.json");
            await using Stream entryStream = smuggled.Open();
            await entryStream.WriteAsync(Encoding.UTF8.GetBytes("{\"language\":\"de-DE\"}"));
        }

        string targetRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "scoped-target")).FullName;
        string targetData = Directory.CreateDirectory(Path.Combine(targetRoot, "data")).FullName;
        string liveSettingsPath = Path.Combine(targetData, "settings.json");
        await File.WriteAllTextAsync(liveSettingsPath, "{\"language\":\"zh-CN\"}");

        var targetService = new DeskBoxDataBackupService(targetRoot);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            targetService.PrepareScopedRestoreAsync(archivePath, CloudBackupDomain.TodoData));
        Assert.Equal("{\"language\":\"zh-CN\"}", await File.ReadAllTextAsync(liveSettingsPath));
    }

    [Fact]
    public async Task CloudBackupService_CanceledUploadDeletesWithUncanceledToken_AndZeroRetentionKeepsNewestSnapshot()
    {
        string appRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "cloud-app")).FullName;
        string dataDir = Directory.CreateDirectory(Path.Combine(appRoot, "data")).FullName;
        await new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "todo-cloud").SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "c1", Text = "cloud item" }]
        });

        var backup = new DeskBoxDataBackupService(appRoot);
        var settings = new SettingsService(Path.Combine(_tempRoot, "cloud-settings"));
        await settings.LoadAsync();
        var credentials = new DictionaryCredentialStore();

        using var uploadCts = new CancellationTokenSource();
        CancellationToken? observedDeleteToken = null;
        string? deletedPartialPath = null;

        var cancelingTransport = new RecordingCloudTransport
        {
            OnUploadAsync = (_, _, ct) =>
            {
                uploadCts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            OnDeleteAsync = (path, ct) =>
            {
                deletedPartialPath = path;
                observedDeleteToken = ct;
                return Task.CompletedTask;
            }
        };

        var options = new CloudBackupOptions(
            Provider: CloudBackupSettingsPolicy.ProviderWebDav,
            ServerUrl: "https://dav.example.com/remote.php/dav/files/u",
            RemotePath: "DeskBox/backups",
            Username: "u",
            Scope: CloudBackupDomain.TodoData,
            RetentionCount: 0,
            IntervalMinutes: 24 * 60,
            LastSuccessUtc: DateTimeOffset.MinValue);

        await credentials.SetSecretAsync(CloudBackupSettingsPolicy.CredentialKey(options), "secret");
        var cancelService = new CloudBackupService(backup, settings, credentials, (_, _) => cancelingTransport);
        cancelService.UpdateOptions(options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelService.RunBackupNowAsync(uploadCts.Token));

        Assert.NotNull(deletedPartialPath);
        Assert.True(observedDeleteToken.HasValue);
        Assert.False(observedDeleteToken.Value.IsCancellationRequested);
        Assert.False(observedDeleteToken.Value.CanBeCanceled);

        // Now verify RetentionCount == 0 clamps to Math.Max(1, 0) == 1 and keeps the newest snapshot.
        var retentionTransport = new RecordingCloudTransport();
        retentionTransport.Files["DeskBox/backups/DeskBox-CloudBackup-20250101T000000Z-11111111.zip"] = [1, 2, 3];
        var retentionService = new CloudBackupService(backup, settings, credentials, (_, _) => retentionTransport);
        retentionService.UpdateOptions(options);

        CloudBackupRunResult runResult = await retentionService.RunBackupNowAsync();
        Assert.True(runResult.Uploaded);
        Assert.Equal(1, runResult.PrunedCount);
        Assert.Single(retentionTransport.Files);
        Assert.True(retentionTransport.Files.ContainsKey(runResult.RemoteFilePath!));
    }

    [Fact]
    public void RestoreMachineLocalPathRebase_RebasesNestedWidgetItemPathsAndFileAddedAtTimestamps()
    {
        const string aliceDataPath = @"C:\Users\alice\AppData\Local\DeskBox\data";
        const string bobProfile = @"C:\Users\bob";
        const string aliceWidgetFolder = @"C:\Users\alice\DeskBox\Work";

        var addedAt = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);
        var fileWidget = new WidgetConfig
        {
            Id = "work-files",
            WidgetKind = WidgetKind.File,
            FollowsDefaultStoragePath = true,
            ManagedFolderName = "Work",
            MappedFolderPath = aliceWidgetFolder,
            Items =
            [
                new WidgetItemConfig
                {
                    Path = @"C:\Users\alice\DeskBox\Work\spec.md"
                }
            ],
            FileAddedAtByPath = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Users\alice\DeskBox\Work\spec.md"] = addedAt
            }
        };
        WidgetFileStackSettings.SetStackMemberOverrides(
            fileWidget,
            new Dictionary<string, List<string>>
            {
                ["Manual:docs"] = [@"C:\Users\alice\DeskBox\Work\spec.md"]
            });

        var settings = new AppSettings
        {
            DefaultManagedStorageRootPath = @"C:\Users\alice\DeskBox"
        };
        settings.WidgetLayout.Widgets.Add(fileWidget);

        RestorePathRebaseResult result = RestoreMachineLocalPathRebase.Rebase(settings, aliceDataPath, bobProfile);

        Assert.True(result.Performed);
        Assert.True(result.Changed);
        Assert.Equal(1, result.WidgetsRebased);
        Assert.Equal(@"C:\Users\bob\DeskBox\Work", fileWidget.MappedFolderPath);
        Assert.Equal(@"C:\Users\bob\DeskBox\Work\spec.md", Assert.Single(fileWidget.Items).Path);
        Assert.True(fileWidget.FileAddedAtByPath.ContainsKey(@"C:\Users\bob\DeskBox\Work\spec.md"));
        Dictionary<string, List<string>> rebasedStacks = WidgetFileStackSettings.GetStackMemberOverrides(fileWidget);
        Assert.Equal(@"C:\Users\bob\DeskBox\Work\spec.md", Assert.Single(rebasedStacks["Manual:docs"]));
    }

    [Fact]
    public async Task SettingsIpcServer_EnforcesExpectedClientIdentity_AndRouterPropagatesHostCommitId()
    {
        using var selfProcess = Process.GetCurrentProcess();
        uint currentPid = (uint)selfProcess.Id;
        DateTime currentStart = selfProcess.StartTime;
        (uint Pid, DateTime? StartTime)? expectedIdentity = (uint.MaxValue - 1, currentStart);
        int dispatchedCount = 0;

        string pipeName = "deskbox-tests-ipc-auth-" + Guid.NewGuid().ToString("N");
        await using var server = SettingsIpcServer.Start(
            pipeName,
            (_, _, _) =>
            {
                Interlocked.Increment(ref dispatchedCount);
                return Task.FromResult<(bool Ok, string? Payload, string? Error)>((true, "pong", null));
            },
            () => expectedIdentity);

        // 1. Mismatched PID must be dropped before dispatching a single request.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var wrongPidClient = await SettingsIpcClient.ConnectAsync(
                pipeName,
                TimeSpan.FromMilliseconds(200),
                attempts: 10);
            _ = await wrongPidClient.RequestAsync(SettingsIpcMethods.Ping);
        });
        Assert.Equal(0, Volatile.Read(ref dispatchedCount));

        // 2. Matching PID with drifted StartTime (> 500ms) must also be rejected (PID reuse defense).
        expectedIdentity = (currentPid, currentStart.AddSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var driftedStartClient = await SettingsIpcClient.ConnectAsync(
                pipeName,
                TimeSpan.FromMilliseconds(200),
                attempts: 10);
            _ = await driftedStartClient.RequestAsync(SettingsIpcMethods.Ping);
        });
        Assert.Equal(0, Volatile.Read(ref dispatchedCount));

        // 3. Exact PID + StartTime succeeds.
        expectedIdentity = (currentPid, currentStart);
        await using (var validClient = await SettingsIpcClient.ConnectAsync(
                         pipeName,
                         TimeSpan.FromMilliseconds(200),
                         attempts: 15))
        {
            (bool ok, string? payload, _) = await validClient.RequestAsync(SettingsIpcMethods.Ping);
            Assert.True(ok);
            Assert.Equal("pong", payload);
        }
        Assert.Equal(1, Volatile.Read(ref dispatchedCount));

        // 4. SettingsIpcMethodRouter tracks hostCommitId during SettingsCommit and rejects malformed Glance payloads.
        string mainDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "router-main")).FullName;
        var mainSettings = new SettingsService(mainDir);
        await mainSettings.LoadAsync();
        long observedCommitIdDuringSave = -1;
        SettingsIpcMethodRouter? router = null;
        router = new SettingsIpcMethodRouter(mainSettings, hotkey: null, data: null);
        mainSettings.LocalPersistCompleted += _ => observedCommitIdDuringSave = router!.LastAppliedHostCommitId;

        string snapshotJson = mainSettings.SerializeCurrentSettings();
        (bool commitOk, _, string? commitErr) = await router.DispatchAsync(
            SettingsIpcMethods.SettingsCommit,
            "42\n" + snapshotJson,
            CancellationToken.None);
        Assert.True(commitOk, commitErr);
        Assert.Equal(42L, observedCommitIdDuringSave);

        (bool malformedOk, _, string? malformedErr) = await router.DispatchAsync(
            SettingsIpcMethods.WidgetGlanceApplyData,
            "widget-1\n{not-json",
            CancellationToken.None);
        Assert.False(malformedOk);
        Assert.Equal("malformed payload", malformedErr);
    }

    private sealed class DictionaryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryGetValue(key, out string? value) ? value : null);

        public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            _secrets.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_secrets.Keys.ToList());
    }

    private sealed class RecordingCloudTransport : ICloudBackupTransport
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public Func<string, Stream, CancellationToken, Task>? OnUploadAsync { get; init; }
        public Func<string, CancellationToken, Task>? OnDeleteAsync { get; init; }

        public Task ProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureDirectoryAsync(string remoteDirectoryPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<CloudBackupRemoteEntry>> ListAsync(
            string remoteDirectoryPath,
            CancellationToken cancellationToken = default)
        {
            string prefix = remoteDirectoryPath.TrimEnd('/') + "/";
            IReadOnlyList<CloudBackupRemoteEntry> entries = Files
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => new CloudBackupRemoteEntry(
                    kv.Key[prefix.Length..],
                    kv.Value.Length,
                    null,
                    IsCollection: false))
                .ToList();
            return Task.FromResult(entries);
        }

        public async Task UploadAsync(string remoteFilePath, Stream content, CancellationToken cancellationToken = default)
        {
            if (OnUploadAsync is not null)
            {
                await OnUploadAsync(remoteFilePath, content, cancellationToken);
                return;
            }

            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            Files[remoteFilePath] = ms.ToArray();
        }

        public async Task DownloadAsync(string remoteFilePath, Stream destination, CancellationToken cancellationToken = default)
        {
            if (!Files.TryGetValue(remoteFilePath, out byte[]? bytes))
            {
                throw new CloudBackupTransportException("not found", HttpStatusCode.NotFound);
            }

            await destination.WriteAsync(bytes, cancellationToken);
        }

        public async Task DeleteAsync(string remoteFilePath, CancellationToken cancellationToken = default)
        {
            if (OnDeleteAsync is not null)
            {
                await OnDeleteAsync(remoteFilePath, cancellationToken);
            }

            Files.Remove(remoteFilePath);
        }
    }
}
