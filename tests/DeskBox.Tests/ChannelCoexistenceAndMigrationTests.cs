using System.Text;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Distribution;

namespace DeskBox.Tests;

public sealed class ChannelCoexistenceAndMigrationTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        $"ChannelCoexistence-{Guid.NewGuid():N}");

    [Fact]
    public void DeferredStoreDataPathConstruction_DoesNotMigrateOrWriteSentinelBeforePrimaryOwnership()
    {
        string fakeLocalAppData = Directory.CreateDirectory(Path.Combine(_sandbox, "LocalAppData")).FullName;
        string fakeUserProfile = Directory.CreateDirectory(Path.Combine(_sandbox, "UserProfile")).FullName;
        string directDataDir = Directory.CreateDirectory(Path.Combine(fakeLocalAppData, "DeskBox", "data")).FullName;
        File.WriteAllText(Path.Combine(directDataDir, "settings.json"), "{\"schemaVersion\":18}", Encoding.UTF8);

        var deferredStorePaths = new DeskBoxDataPathService(
            rootPath: null,
            isMicrosoftStore: true,
            localAppDataPath: fakeLocalAppData,
            userProfilePath: fakeUserProfile,
            deferStoreMigration: true);

        string durableRoot = Path.Combine(fakeUserProfile, "DeskBox", DeskBoxDataPathService.StoreDurableSubdirectoryName);
        Assert.Equal(durableRoot, deferredStorePaths.RootPath);
        Assert.False(File.Exists(Path.Combine(durableRoot, StoreUserAssetMigrationFileOps.MigrationSentinelFileName)));
        Assert.False(File.Exists(Path.Combine(durableRoot, "data", "settings.json")));

        deferredStorePaths.EnsureStoreDurableRootInitialized();

        Assert.True(File.Exists(Path.Combine(durableRoot, StoreUserAssetMigrationFileOps.MigrationSentinelFileName)));
        Assert.True(File.Exists(Path.Combine(durableRoot, "data", "settings.json")));
    }

    [Fact]
    public void DirectToStoreInitialMigration_CopiesDataAndRebasesSettingsLayoutAndAttachments()
    {
        string directRoot = Path.Combine(_sandbox, "DirectRoot");
        string directData = Directory.CreateDirectory(Path.Combine(directRoot, "data")).FullName;
        string directAssets = Directory.CreateDirectory(Path.Combine(directData, "widget-assets")).FullName;
        string directTodoDir = Directory.CreateDirectory(Path.Combine(directData, "widgets", "todo-1", "attachments")).FullName;
        string storeRoot = Path.Combine(_sandbox, "UserProfile", "DeskBox", ".appdata");
        string storeRecovery = Path.Combine(_sandbox, "UserProfile", "DeskBox", ".appdata-recovery");

        string assetFile = Path.Combine(directAssets, "bg.png");
        string todoAttachment = Path.Combine(directTodoDir, "spec.pdf");
        File.WriteAllText(assetFile, "png", Encoding.UTF8);
        File.WriteAllText(todoAttachment, "pdf", Encoding.UTF8);

        string escapedAsset = assetFile.Replace("\\", "\\\\", StringComparison.Ordinal);
        string escapedAttachment = todoAttachment.Replace("\\", "\\\\", StringComparison.Ordinal);

        File.WriteAllText(
            Path.Combine(directData, "settings.json"),
            $"{{\"schemaVersion\":18,\"defaultManagedStorageRootPath\":\"D:\\\\DeskBox\",\"customAsset\":\"{escapedAsset}\"}}",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(directData, "widget-layout.json"),
            $"{{\"schemaVersion\":1,\"layout\":{{\"widgets\":[{{\"id\":\"w1\",\"widgetKind\":\"File\",\"mappedFolderPath\":\"D:\\\\DeskBox\\\\Work\",\"backgroundImagePath\":\"{escapedAsset}\"}}]}}}}",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(directData, "widgets", "todo-1", "todo.json"),
            $"{{\"items\":[{{\"attachments\":[{{\"filePath\":\"{escapedAttachment}\"}}]}}]}}",
            Encoding.UTF8);

        int promptCalls = 0;
        ChannelMigrationOutcome outcome = ChannelMigrationService.RunStoreStartupMigration(
            directRoot,
            storeRoot,
            Path.Combine(_sandbox, "DirectRecovery"),
            storeRecovery,
            isInteractiveLaunch: true,
            cultureName: "zh-CN",
            confirmPrompt: (_, _) => { promptCalls++; return true; },
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path => path);

        Assert.Equal(ChannelMigrationOutcomeKind.MigratedFromDirectToStoreInitial, outcome.Kind);
        Assert.True(outcome.OtherChannelInstalled);
        Assert.Equal(0, promptCalls);

        string storeData = Path.Combine(storeRoot, "data");
        string expectedStoreAsset = Path.Combine(storeData, "widget-assets", "bg.png");
        string expectedStoreAttachment = Path.Combine(storeData, "widgets", "todo-1", "attachments", "spec.pdf");
        Assert.True(File.Exists(expectedStoreAsset));
        Assert.True(File.Exists(expectedStoreAttachment));

        string migratedSettings = File.ReadAllText(Path.Combine(storeData, "settings.json"), Encoding.UTF8);
        string migratedLayout = File.ReadAllText(Path.Combine(storeData, "widget-layout.json"), Encoding.UTF8);
        string migratedTodo = File.ReadAllText(Path.Combine(storeData, "widgets", "todo-1", "todo.json"), Encoding.UTF8);

        Assert.Contains(expectedStoreAsset.Replace("\\", "\\\\", StringComparison.Ordinal), migratedSettings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("D:\\\\DeskBox", migratedSettings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedStoreAsset.Replace("\\", "\\\\", StringComparison.Ordinal), migratedLayout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("D:\\\\DeskBox\\\\Work", migratedLayout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedStoreAttachment.Replace("\\", "\\\\", StringComparison.Ordinal), migratedTodo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyStoreLocalCache_MigratesSilentlyWithoutDirectHint()
    {
        string legacyRoot = Path.Combine(_sandbox, "LegacyLocalCache");
        string legacyData = Directory.CreateDirectory(Path.Combine(legacyRoot, "data")).FullName;
        string storeRoot = Path.Combine(_sandbox, "StoreProfile", "DeskBox", ".appdata");
        File.WriteAllText(Path.Combine(legacyData, "settings.json"), "{\"schemaVersion\":18}", Encoding.UTF8);

        ChannelMigrationOutcome outcome = ChannelMigrationService.RunStoreStartupMigration(
            legacyRoot,
            storeRoot,
            Path.Combine(_sandbox, "LegacyRecovery"),
            Path.Combine(_sandbox, "StoreProfile", "DeskBox", ".appdata-recovery"),
            isInteractiveLaunch: true,
            cultureName: "en-US",
            confirmPrompt: (_, _) => throw new InvalidOperationException("Legacy LocalCache migration must not prompt."),
            otherChannelInstalledProbe: () => false,
            resolveFinalPath: _ => @"C:\Users\alice\AppData\Local\Packages\D1FC332A.DeskBoxWidgets_abc\LocalCache\Local\DeskBox\data");

        Assert.Equal(ChannelMigrationOutcomeKind.MigratedFromLegacyStoreLocalCache, outcome.Kind);
        Assert.True(File.Exists(Path.Combine(storeRoot, "data", "settings.json")));
    }

    [Fact]
    public void DirectToStoreNewerData_BacksUpStoreProfileAndReimportsWhenConfirmed()
    {
        string directRoot = Path.Combine(_sandbox, "DirectNewer");
        string directData = Directory.CreateDirectory(Path.Combine(directRoot, "data")).FullName;
        string storeRoot = Path.Combine(_sandbox, "StoreOlder");
        string storeData = Directory.CreateDirectory(Path.Combine(storeRoot, "data")).FullName;
        string storeRecovery = Path.Combine(_sandbox, "StoreRecovery");

        string storeSettings = Path.Combine(storeData, "settings.json");
        string directSettings = Path.Combine(directData, "settings.json");
        File.WriteAllText(storeSettings, "{\"schemaVersion\":18,\"marker\":\"old-store\"}", Encoding.UTF8);
        File.WriteAllText(directSettings, "{\"schemaVersion\":18,\"marker\":\"new-direct\"}", Encoding.UTF8);
        File.SetLastWriteTimeUtc(storeSettings, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(directSettings, DateTime.UtcNow.AddMinutes(-1));

        int promptCalls = 0;
        ChannelMigrationOutcome first = ChannelMigrationService.RunStoreStartupMigration(
            directRoot,
            storeRoot,
            Path.Combine(_sandbox, "DirectRec"),
            storeRecovery,
            isInteractiveLaunch: true,
            cultureName: "zh-CN",
            confirmPrompt: (_, _) => { promptCalls++; return true; },
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path => path);

        Assert.Equal(ChannelMigrationOutcomeKind.MigratedFromDirectToStoreNewer, first.Kind);
        Assert.Equal(1, promptCalls);
        Assert.NotNull(first.BackupDirectory);
        Assert.Contains("old-store", File.ReadAllText(Path.Combine(first.BackupDirectory!, "settings.json"), Encoding.UTF8));
        Assert.Contains("new-direct", File.ReadAllText(storeSettings, Encoding.UTF8));

        // Second launch with unchanged Direct files must honor watermark and not prompt again.
        ChannelMigrationOutcome second = ChannelMigrationService.RunStoreStartupMigration(
            directRoot,
            storeRoot,
            Path.Combine(_sandbox, "DirectRec"),
            storeRecovery,
            isInteractiveLaunch: true,
            cultureName: "zh-CN",
            confirmPrompt: (_, _) => { promptCalls++; return true; },
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path => path);

        Assert.Equal(ChannelMigrationOutcomeKind.None, second.Kind);
        Assert.Equal(1, promptCalls);
    }

    [Fact]
    public void DirectToStoreNewerData_DeclinePreservesStoreAndSuppressesRepeatPrompt()
    {
        string directRoot = Path.Combine(_sandbox, "DirectDecline");
        string directData = Directory.CreateDirectory(Path.Combine(directRoot, "data")).FullName;
        string storeRoot = Path.Combine(_sandbox, "StoreKeep");
        string storeData = Directory.CreateDirectory(Path.Combine(storeRoot, "data")).FullName;

        string storeSettings = Path.Combine(storeData, "settings.json");
        string directSettings = Path.Combine(directData, "settings.json");
        File.WriteAllText(storeSettings, "{\"schemaVersion\":18,\"marker\":\"keep-store\"}", Encoding.UTF8);
        File.WriteAllText(directSettings, "{\"schemaVersion\":18,\"marker\":\"newer-direct\"}", Encoding.UTF8);
        File.SetLastWriteTimeUtc(storeSettings, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(directSettings, DateTime.UtcNow.AddMinutes(-1));

        int promptCalls = 0;
        ChannelMigrationOutcome declined = ChannelMigrationService.RunStoreStartupMigration(
            directRoot,
            storeRoot,
            Path.Combine(_sandbox, "DirectRec2"),
            Path.Combine(_sandbox, "StoreRec2"),
            isInteractiveLaunch: true,
            cultureName: "en-US",
            confirmPrompt: (_, _) => { promptCalls++; return false; },
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path => path);

        Assert.Equal(ChannelMigrationOutcomeKind.DeclinedByUser, declined.Kind);
        Assert.Contains("keep-store", File.ReadAllText(storeSettings, Encoding.UTF8));

        ChannelMigrationOutcome repeat = ChannelMigrationService.RunStoreStartupMigration(
            directRoot,
            storeRoot,
            Path.Combine(_sandbox, "DirectRec2"),
            Path.Combine(_sandbox, "StoreRec2"),
            isInteractiveLaunch: true,
            cultureName: "en-US",
            confirmPrompt: (_, _) => { promptCalls++; return true; },
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path => path);

        Assert.Equal(ChannelMigrationOutcomeKind.None, repeat.Kind);
        Assert.Equal(1, promptCalls);
    }

    [Fact]
    public void StoreToDirectFirstLaunch_ImportsAndRebasesStoreProfileWhenConfirmed()
    {
        string directRoot = Path.Combine(_sandbox, "EmptyDirect");
        string storeRoot = Path.Combine(_sandbox, "PopulatedStore");
        string storeData = Directory.CreateDirectory(Path.Combine(storeRoot, "data")).FullName;
        string storeAssets = Directory.CreateDirectory(Path.Combine(storeData, "widget-assets")).FullName;
        string storeBg = Path.Combine(storeAssets, "cover.jpg");
        File.WriteAllText(storeBg, "jpg", Encoding.UTF8);

        string escapedStoreBg = storeBg.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(storeData, "settings.json"),
            $"{{\"schemaVersion\":18,\"defaultManagedStorageRootPath\":\"F:\\\\Box\",\"bg\":\"{escapedStoreBg}\"}}",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(storeData, "widget-layout.json"),
            $"{{\"schemaVersion\":1,\"layout\":{{\"widgets\":[{{\"id\":\"w1\",\"widgetKind\":\"File\",\"mappedFolderPath\":\"F:\\\\Box\\\\Docs\",\"bg\":\"{escapedStoreBg}\"}}]}}}}",
            Encoding.UTF8);

        ChannelMigrationOutcome imported = ChannelMigrationService.RunDirectStartupImportFromStore(
            directRoot,
            storeRoot,
            isInteractiveLaunch: true,
            cultureName: "zh-CN",
            confirmPrompt: (_, _) => true,
            otherChannelInstalledProbe: () => true);

        Assert.Equal(ChannelMigrationOutcomeKind.ImportedFromStoreToDirect, imported.Kind);
        Assert.True(imported.OtherChannelInstalled);

        string directData = Path.Combine(directRoot, "data");
        string expectedDirectBg = Path.Combine(directData, "widget-assets", "cover.jpg");
        Assert.True(File.Exists(expectedDirectBg));
        string importedSettings = File.ReadAllText(Path.Combine(directData, "settings.json"), Encoding.UTF8);
        Assert.Contains(expectedDirectBg.Replace("\\", "\\\\", StringComparison.Ordinal), importedSettings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("F:\\\\Box", importedSettings, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StoreToDirectFirstLaunch_DeclineWritesMarkerAndDoesNotReprompt()
    {
        string directRoot = Path.Combine(_sandbox, "FreshDirect");
        string storeRoot = Path.Combine(_sandbox, "ExistingStore");
        string storeData = Directory.CreateDirectory(Path.Combine(storeRoot, "data")).FullName;
        File.WriteAllText(Path.Combine(storeData, "settings.json"), "{\"schemaVersion\":18}", Encoding.UTF8);

        int promptCalls = 0;
        ChannelMigrationOutcome first = ChannelMigrationService.RunDirectStartupImportFromStore(
            directRoot,
            storeRoot,
            isInteractiveLaunch: true,
            cultureName: "en-US",
            confirmPrompt: (_, _) => { promptCalls++; return false; },
            otherChannelInstalledProbe: () => true);

        Assert.Equal(ChannelMigrationOutcomeKind.DeclinedByUser, first.Kind);
        Assert.False(File.Exists(Path.Combine(directRoot, "data", "settings.json")));
        Assert.True(File.Exists(Path.Combine(directRoot, ChannelMigrationService.StoreImportDeclinedMarkerFileName)));

        ChannelMigrationOutcome second = ChannelMigrationService.RunDirectStartupImportFromStore(
            directRoot,
            storeRoot,
            isInteractiveLaunch: true,
            cultureName: "en-US",
            confirmPrompt: (_, _) => { promptCalls++; return true; },
            otherChannelInstalledProbe: () => true);

        Assert.Equal(ChannelMigrationOutcomeKind.None, second.Kind);
        Assert.Equal(1, promptCalls);
    }

    [Fact]
    public void PrimaryOwnerMappingAndGracefulExit_TransfersPrimaryMutexCleanly()
    {
        string uniqueRoot = Path.Combine(_sandbox, "HandoffRoot");
        var dataPaths = new DeskBoxDataPathService(uniqueRoot);

        (Mutex? initialMutex, bool createdNew) = SingleInstanceAndActivationInterop.CreateOrOpenSingleInstanceMutex(
            dataPaths.SingleInstanceMutexName);
        Assert.True(createdNew);
        Assert.NotNull(initialMutex);

        using var ownerMapping = ChannelCoexistenceService.PublishPrimaryInstanceOwner(
            dataPaths,
            AppDistributionChannel.Direct,
            Environment.ProcessId,
            @"C:\Program Files\DeskBox\DeskBox.exe");
        Assert.NotNull(ownerMapping);

        PrimaryInstanceOwnerInfo? parsed = ChannelCoexistenceService.TryReadPrimaryInstanceOwner(
            dataPaths,
            allowCurrentProcess: true);
        Assert.NotNull(parsed);
        Assert.Equal(AppDistributionChannel.Direct, parsed!.Channel);
        Assert.Equal(Environment.ProcessId, parsed.ProcessId);
        Assert.Equal(dataPaths.BuildGracefulExitEventName(Environment.ProcessId), parsed.GracefulExitEventName);

        using EventWaitHandle? exitEvent = SingleInstanceAndActivationInterop.CreateOrOpenActivationEvent(
            parsed.GracefulExitEventName);
        Assert.NotNull(exitEvent);

        // Release the initial mutex so the simulated primary thread owns it
        // (Win32 Mutex must be released on the same thread that acquired it).
        initialMutex!.ReleaseMutex();
        initialMutex.Dispose();

        var releaseDone = new ManualResetEventSlim(false);
        var threadHasMutex = new ManualResetEventSlim(false);
        var ownerThread = new Thread(() =>
        {
            (Mutex? threadMutex, bool acquired) = SingleInstanceAndActivationInterop.CreateOrOpenSingleInstanceMutex(
                dataPaths.SingleInstanceMutexName);
            if (!acquired || threadMutex is null)
            {
                return;
            }

            threadHasMutex.Set();
            try
            {
                if (exitEvent.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    threadMutex.ReleaseMutex();
                }
            }
            finally
            {
                threadMutex.Dispose();
                releaseDone.Set();
            }
        });
        ownerThread.Start();
        Assert.True(threadHasMutex.Wait(TimeSpan.FromSeconds(3)));

        (Mutex? handedOff, bool transferred) = ChannelCoexistenceService.TrySignalGracefulExitAndAcquirePrimaryMutex(
            parsed,
            dataPaths.SingleInstanceMutexName,
            timeoutMs: 5000);
        try
        {
            Assert.True(transferred);
            Assert.NotNull(handedOff);
            Assert.True(releaseDone.Wait(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            if (handedOff is not null)
            {
                try { handedOff.ReleaseMutex(); } catch { }
                handedOff.Dispose();
            }
            ownerThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task OrphanCleanup_ExcludesAndProtectsFoldersOwnedBySiblingChannelWidgets()
    {
        string sharedStorageRoot = Directory.CreateDirectory(Path.Combine(_sandbox, "SharedDeskBox")).FullName;
        string storeOwnedFolder = Directory.CreateDirectory(Path.Combine(sharedStorageRoot, "StoreProjects")).FullName;
        string trueOrphanFolder = Directory.CreateDirectory(Path.Combine(sharedStorageRoot, "OldDeleted")).FullName;
        string storeImportantFile = Path.Combine(storeOwnedFolder, "contract.docx");
        File.WriteAllText(storeImportantFile, "keep-safe", Encoding.UTF8);

        ManagedStorageOwnershipFileOps.RegisterManagedFolders(
            sharedStorageRoot,
            ["StoreProjects", "OldDeleted"],
            ManagedStorageMigrationService.TemporaryFolderName);

        string siblingRoot = Path.Combine(_sandbox, "SiblingChannel");
        string siblingData = Directory.CreateDirectory(Path.Combine(siblingRoot, "data")).FullName;
        string escapedStoreFolder = storeOwnedFolder.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(siblingData, "widget-layout.json"),
            $"{{\"schemaVersion\":1,\"layout\":{{\"deletedWidgetIds\":[],\"widgets\":[{{\"id\":\"store-w1\",\"widgetKind\":\"File\",\"followsDefaultStoragePath\":true,\"managedFolderName\":\"StoreProjects\",\"mappedFolderPath\":\"{escapedStoreFolder}\"}}]}}}}"
            , Encoding.UTF8);

        var directSettings = new SettingsService(Path.Combine(_sandbox, "DirectSettings"));
        await directSettings.LoadAsync();
        directSettings.Settings.FileWidget.DefaultManagedStorageRootPath = sharedStorageRoot;

        var fileService = new FileService();
        var widgetManager = new WidgetManager(
            directSettings,
            fileService,
            TestOrganizerServices.Create(directSettings, fileService),
            new ThemeService(directSettings),
            new QuickCaptureService(new QuickCaptureStore(Path.Combine(_sandbox, "Notes"))),
            () => Path.Combine(_sandbox, "Desktop"),
            recycleManagedFolderDeletes: false)
        {
            SiblingChannelActivePathsProvider = () =>
                ChannelCoexistenceService.ReadActiveManagedFolderPathsFromDataRoot(siblingRoot)
        };

        IReadOnlyList<ManagedStorageFolderCleanupCandidate> orphans = widgetManager.GetOrphanManagedStorageFolders();
        ManagedStorageFolderCleanupCandidate onlyOrphan = Assert.Single(orphans);
        Assert.Equal("OldDeleted", onlyOrphan.Name);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            widgetManager.DeleteOrphanManagedStorageFolderAsync(storeOwnedFolder));
        Assert.True(File.Exists(storeImportantFile));
    }

    [Fact]
    public void TryReadPrimaryInstanceOwner_RejectsDeadOrSelfProcessId()
    {
        string selfRoot = Path.Combine(_sandbox, "SelfOwnerRoot");
        var selfPaths = new DeskBoxDataPathService(selfRoot);

        using (var selfMapping = ChannelCoexistenceService.PublishPrimaryInstanceOwner(
                   selfPaths,
                   AppDistributionChannel.Direct,
                   Environment.ProcessId,
                   @"C:\Program Files\DeskBox\DeskBox.exe"))
        {
            Assert.NotNull(selfMapping);
            Assert.Null(ChannelCoexistenceService.TryReadPrimaryInstanceOwner(
                selfPaths,
                processAliveProbe: _ => true));
        }

        string peerRoot = Path.Combine(_sandbox, "PeerOwnerRoot");
        var peerPaths = new DeskBoxDataPathService(peerRoot);
        int peerPid = Environment.ProcessId + 10000;

        using (var peerMapping = ChannelCoexistenceService.PublishPrimaryInstanceOwner(
                   peerPaths,
                   AppDistributionChannel.MicrosoftStore,
                   peerPid,
                   @"C:\Program Files\WindowsApps\D1FC332A.DeskBoxWidgets_1.6.0.0_x64__abc\DeskBox.exe"))
        {
            Assert.NotNull(peerMapping);
            Assert.Null(ChannelCoexistenceService.TryReadPrimaryInstanceOwner(
                peerPaths,
                processAliveProbe: _ => false));

            PrimaryInstanceOwnerInfo? live = ChannelCoexistenceService.TryReadPrimaryInstanceOwner(
                peerPaths,
                processAliveProbe: pid => pid == peerPid);
            Assert.NotNull(live);
            Assert.Equal(AppDistributionChannel.MicrosoftStore, live!.Channel);
            Assert.Equal(peerPid, live.ProcessId);
        }
    }

    [Fact]
    public void TryDetectLegacyRunningOppositeChannelProcess_ClassifiesWindowsAppsVsDirectInstall()
    {
        Assert.Equal(
            AppDistributionChannel.MicrosoftStore,
            ChannelCoexistenceService.ClassifyExecutableChannel(
                @"C:\Program Files\WindowsApps\D1FC332A.DeskBoxWidgets_1.5.9.0_x64__abc\DeskBox.exe"));
        Assert.Equal(
            AppDistributionChannel.MicrosoftStore,
            ChannelCoexistenceService.ClassifyExecutableChannel(
                @"D:\WindowsApps\D1FC332A.DeskBoxWidgets_1.5.9.0_arm64__abc\DeskBox.exe"));
        Assert.Equal(
            AppDistributionChannel.Direct,
            ChannelCoexistenceService.ClassifyExecutableChannel(@"C:\Program Files\DeskBox\DeskBox.exe"));
        Assert.Null(ChannelCoexistenceService.ClassifyExecutableChannel("   "));

        int fakeSelf = 1001;
        var mixedProcesses = new (int Pid, string ExecutablePath)[]
        {
            (fakeSelf, @"C:\Program Files\WindowsApps\D1FC332A.DeskBoxWidgets_1.6.0.0_x64__abc\DeskBox.exe"),
            (2002, @"C:\Program Files\WindowsApps\D1FC332A.DeskBoxWidgets_1.6.0.0_x64__abc\DeskBox.exe"),
            (3003, @"C:\Program Files\DeskBox\DeskBox.exe"),
        };

        LegacyRunningChannelProcess? detectedDirect =
            ChannelCoexistenceService.TryDetectLegacyRunningOppositeChannelProcess(
                AppDistributionChannel.MicrosoftStore,
                processEnumerator: () => mixedProcesses,
                currentProcessIdOverride: fakeSelf);
        Assert.NotNull(detectedDirect);
        Assert.Equal(AppDistributionChannel.Direct, detectedDirect!.Channel);
        Assert.Equal(3003, detectedDirect.ProcessId);

        LegacyRunningChannelProcess? detectedStore =
            ChannelCoexistenceService.TryDetectLegacyRunningOppositeChannelProcess(
                AppDistributionChannel.Direct,
                processEnumerator: () => mixedProcesses,
                currentProcessIdOverride: 3003);
        Assert.NotNull(detectedStore);
        Assert.Equal(AppDistributionChannel.MicrosoftStore, detectedStore!.Channel);
        Assert.Equal(fakeSelf, detectedStore.ProcessId);

        LegacyRunningChannelProcess? noneFound =
            ChannelCoexistenceService.TryDetectLegacyRunningOppositeChannelProcess(
                AppDistributionChannel.Direct,
                processEnumerator: () => new[] { (3003, @"C:\Program Files\DeskBox\DeskBox.exe") },
                currentProcessIdOverride: 4004);
        Assert.Null(noneFound);
    }

    [Fact]
    public void ChannelMigration_SkipsWhenSourceSchemaVersionExceedsCurrentBuild()
    {
        // Path A: Direct -> Store initial migration blocked when Direct schemaVersion > supported ceiling.
        string directInitialRoot = Path.Combine(_sandbox, "DirectFutureInitial");
        string directInitialData = Directory.CreateDirectory(Path.Combine(directInitialRoot, "data")).FullName;
        string storeEmptyRoot = Path.Combine(_sandbox, "StoreEmptyInitial");
        File.WriteAllText(
            Path.Combine(directInitialData, "settings.json"),
            "{\"schemaVersion\":99,\"marker\":\"future-direct\"}",
            Encoding.UTF8);

        int unattendedNotices = 0;
        ChannelMigrationOutcome unattendedA = ChannelMigrationService.RunStoreStartupMigration(
            directInitialRoot,
            storeEmptyRoot,
            Path.Combine(_sandbox, "DirectRecA"),
            Path.Combine(_sandbox, "StoreRecA"),
            isInteractiveLaunch: false,
            cultureName: "zh-CN",
            confirmPrompt: (_, _) => throw new InvalidOperationException("Should not prompt on schema ceiling skip."),
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path => path,
            infoNotice: (_, _) => unattendedNotices++,
            maxSupportedSchemaVersion: 18);
        Assert.Equal(ChannelMigrationOutcomeKind.SkippedNewerSchema, unattendedA.Kind);
        Assert.False(unattendedA.NotifiedPreUi);
        Assert.Equal(0, unattendedNotices);
        Assert.False(File.Exists(Path.Combine(storeEmptyRoot, "data", "settings.json")));
        Assert.False(File.Exists(Path.Combine(storeEmptyRoot, StoreUserAssetMigrationFileOps.MigrationSentinelFileName)));

        int interactiveNotices = 0;
        ChannelMigrationOutcome interactiveA = ChannelMigrationService.RunStoreStartupMigration(
            directInitialRoot,
            storeEmptyRoot,
            Path.Combine(_sandbox, "DirectRecA"),
            Path.Combine(_sandbox, "StoreRecA"),
            isInteractiveLaunch: true,
            cultureName: "en-US",
            confirmPrompt: (_, _) => throw new InvalidOperationException("Should not prompt on schema ceiling skip."),
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path => path,
            infoNotice: (_, _) => interactiveNotices++,
            maxSupportedSchemaVersion: 18);
        Assert.Equal(ChannelMigrationOutcomeKind.SkippedNewerSchema, interactiveA.Kind);
        Assert.True(interactiveA.NotifiedPreUi);
        Assert.Equal(1, interactiveNotices);
        Assert.False(File.Exists(Path.Combine(storeEmptyRoot, "data", "settings.json")));
        Assert.False(File.Exists(Path.Combine(storeEmptyRoot, StoreUserAssetMigrationFileOps.MigrationSentinelFileName)));

        // Path B: Direct -> Store newer-data re-import blocked when Direct schemaVersion > supported ceiling.
        string directNewerRoot = Path.Combine(_sandbox, "DirectFutureNewer");
        string directNewerData = Directory.CreateDirectory(Path.Combine(directNewerRoot, "data")).FullName;
        string storePopulatedRoot = Path.Combine(_sandbox, "StorePopulatedExisting");
        string storePopulatedData = Directory.CreateDirectory(Path.Combine(storePopulatedRoot, "data")).FullName;
        string storeSettings = Path.Combine(storePopulatedData, "settings.json");
        string directNewerSettings = Path.Combine(directNewerData, "settings.json");
        File.WriteAllText(storeSettings, "{\"schemaVersion\":18,\"marker\":\"keep-store\"}", Encoding.UTF8);
        File.WriteAllText(directNewerSettings, "{\"schemaVersion\":99,\"marker\":\"future-direct\"}", Encoding.UTF8);
        File.SetLastWriteTimeUtc(storeSettings, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(directNewerSettings, DateTime.UtcNow.AddMinutes(-1));

        ChannelMigrationOutcome pathB = ChannelMigrationService.RunStoreStartupMigration(
            directNewerRoot,
            storePopulatedRoot,
            Path.Combine(_sandbox, "DirectRecB"),
            Path.Combine(_sandbox, "StoreRecB"),
            isInteractiveLaunch: true,
            cultureName: "zh-CN",
            confirmPrompt: (_, _) => throw new InvalidOperationException("Should not prompt when Direct schema is newer."),
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path => path,
            infoNotice: (_, _) => throw new InvalidOperationException("Path B defers notification to non-blocking UI toast."),
            maxSupportedSchemaVersion: 18);
        Assert.Equal(ChannelMigrationOutcomeKind.SkippedNewerSchema, pathB.Kind);
        Assert.False(pathB.NotifiedPreUi);
        Assert.Contains("keep-store", File.ReadAllText(storeSettings, Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(storePopulatedRoot, ChannelMigrationService.DirectSyncWatermarkFileName)));

        // Path C: Store -> Direct first-launch import blocked when Store schemaVersion > supported ceiling.
        string directEmptyRoot = Path.Combine(_sandbox, "DirectEmptyC");
        string storeFutureRoot = Path.Combine(_sandbox, "StoreFutureC");
        string storeFutureData = Directory.CreateDirectory(Path.Combine(storeFutureRoot, "data")).FullName;
        File.WriteAllText(
            Path.Combine(storeFutureData, "settings.json"),
            "{\"schemaVersion\":99,\"marker\":\"future-store\"}",
            Encoding.UTF8);

        int pathCNotices = 0;
        ChannelMigrationOutcome pathC = ChannelMigrationService.RunDirectStartupImportFromStore(
            directEmptyRoot,
            storeFutureRoot,
            isInteractiveLaunch: true,
            cultureName: "ar-SA",
            confirmPrompt: (_, _) => throw new InvalidOperationException("Should not prompt when Store schema is newer."),
            otherChannelInstalledProbe: () => true,
            infoNotice: (_, _) => pathCNotices++,
            maxSupportedSchemaVersion: 18);
        Assert.Equal(ChannelMigrationOutcomeKind.SkippedNewerSchema, pathC.Kind);
        Assert.True(pathC.NotifiedPreUi);
        Assert.Equal(1, pathCNotices);
        Assert.False(File.Exists(Path.Combine(directEmptyRoot, "data", "settings.json")));
        Assert.False(File.Exists(Path.Combine(directEmptyRoot, ChannelMigrationService.StoreImportDeclinedMarkerFileName)));
    }

    [Fact]
    public void StaleEmptySentinel_DoesNotBlockDirectToStoreMigrationWhenStoreSettingsMissing()
    {
        string directRoot = Path.Combine(_sandbox, "DirectRealProfile");
        string directData = Directory.CreateDirectory(Path.Combine(directRoot, "data")).FullName;
        string storeRoot = Directory.CreateDirectory(Path.Combine(_sandbox, "StoreStaleSentinel")).FullName;

        // Simulate a stale sentinel left behind by an earlier empty Store run.
        File.WriteAllText(
            Path.Combine(storeRoot, StoreUserAssetMigrationFileOps.MigrationSentinelFileName),
            "v1\n",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(directData, "settings.json"),
            "{\"schemaVersion\":18,\"marker\":\"real-direct-profile\"}",
            Encoding.UTF8);

        ChannelMigrationOutcome outcome = ChannelMigrationService.RunStoreStartupMigration(
            directRoot,
            storeRoot,
            Path.Combine(_sandbox, "DirectRecStale"),
            Path.Combine(_sandbox, "StoreRecStale"),
            isInteractiveLaunch: true,
            cultureName: "zh-CN",
            confirmPrompt: (_, _) => true,
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path => path);

        Assert.Equal(ChannelMigrationOutcomeKind.MigratedFromDirectToStoreInitial, outcome.Kind);
        string migratedStoreSettings = Path.Combine(storeRoot, "data", "settings.json");
        Assert.True(File.Exists(migratedStoreSettings));
        Assert.Contains("real-direct-profile", File.ReadAllText(migratedStoreSettings, Encoding.UTF8));

        // Once durable Store settings exist alongside the sentinel, EnsureStoreDurableRootInitialized
        // must remain a strict no-op and never clobber the Store profile.
        File.WriteAllText(migratedStoreSettings, "{\"schemaVersion\":18,\"marker\":\"store-edited\"}", Encoding.UTF8);
        StoreUserAssetMigrationFileOps.EnsureStoreDurableRootInitialized(
            directRoot,
            storeRoot,
            Path.Combine(_sandbox, "DirectRecStale"),
            Path.Combine(_sandbox, "StoreRecStale"));
        Assert.Contains("store-edited", File.ReadAllText(migratedStoreSettings, Encoding.UTF8));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox))
            {
                Directory.Delete(_sandbox, recursive: true);
            }
        }
        catch
        {
        }
    }
}
