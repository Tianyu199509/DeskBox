using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class FileTransferAtomicitySafetyTests : IDisposable
{
    private readonly string _sandbox;

    public FileTransferAtomicitySafetyTests()
    {
        _sandbox = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "Atomicity_" + Guid.NewGuid().ToString("N"))).FullName;
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

    [Fact]
    public void CrossVolumeAndSameVolumeLockErrorClassifiers_DistinguishNotSameDeviceFromLocks()
    {
        var notSameDevice = new IOException("cross-device", unchecked((int)0x80070011));
        var sharingViolation = new IOException("sharing", unchecked((int)0x80070020));
        var lockViolation = new IOException("lock", unchecked((int)0x80070021));
        var accessDenied = new UnauthorizedAccessException("denied");

        Assert.True(FileService.IsCrossVolumeMoveError(notSameDevice));
        Assert.False(FileService.IsCrossVolumeMoveError(sharingViolation));
        Assert.False(FileService.IsCrossVolumeMoveError(accessDenied));

        Assert.False(FileService.IsSameVolumeLockOrAccessError(notSameDevice));
        Assert.True(FileService.IsSameVolumeLockOrAccessError(sharingViolation));
        Assert.True(FileService.IsSameVolumeLockOrAccessError(lockViolation));
        Assert.True(FileService.IsSameVolumeLockOrAccessError(accessDenied));
    }

    [Fact]
    public async Task HeadlessSameVolumeDirectoryMove_WhenChildFileIsLocked_FailsCleanlyWithoutSplittingTree()
    {
        var service = new FileService();
        string sourceDir = Directory.CreateDirectory(Path.Combine(_sandbox, "src", "Project")).FullName;
        string unlockedFile = Path.Combine(sourceDir, "01-unlocked.txt");
        string lockedFile = Path.Combine(sourceDir, "02-locked.txt");
        File.WriteAllText(unlockedFile, "must-not-split");
        File.WriteAllText(lockedFile, "held-open");
        string destDir = Path.Combine(_sandbox, "dst", "Project");

        await using (var lockStream = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<FileService.FileTransferPartialFailureException>(() =>
                service.ExecuteTransferPlanAsync(
                    [new FileService.FileTransferPlan(sourceDir, destDir)],
                    move: true,
                    useShellProgress: false,
                    ownerWindowHandle: IntPtr.Zero));
        }

        // Source tree stays 100% intact and no partial destination copy was created.
        Assert.True(Directory.Exists(sourceDir));
        Assert.True(File.Exists(unlockedFile));
        Assert.Equal("must-not-split", File.ReadAllText(unlockedFile));
        Assert.True(File.Exists(lockedFile));
        Assert.False(Directory.Exists(destDir));
    }

    [Fact]
    public async Task HeadlessRelocateDirectoryAsync_WhenChildFileIsLocked_FailsCleanlyWithoutSplittingTree()
    {
        var service = new FileService();
        string sourceDir = Directory.CreateDirectory(Path.Combine(_sandbox, "reloc-src", "Folder")).FullName;
        string unlockedFile = Path.Combine(sourceDir, "a.txt");
        string lockedFile = Path.Combine(sourceDir, "b.txt");
        File.WriteAllText(unlockedFile, "intact");
        File.WriteAllText(lockedFile, "locked");
        string destDir = Path.Combine(_sandbox, "reloc-dst", "Folder");

        await using (var lockStream = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.RelocateDirectoryAsync(
                    sourceDir,
                    destDir,
                    progress: null,
                    CancellationToken.None,
                    onItemError: null));
        }

        Assert.True(Directory.Exists(sourceDir));
        Assert.True(File.Exists(unlockedFile));
        Assert.True(File.Exists(lockedFile));
        Assert.False(Directory.Exists(destDir));
    }

    [Fact]
    public async Task WatchdogAbandonedShellTransfer_RetainsSessionLeaseUntilWedgedStaWorkerExits()
    {
        var registry = new FileTransferSessionRegistry();
        string source = Path.Combine(_sandbox, "wedged-source.bin");
        string destination = Path.Combine(_sandbox, "target", "wedged-source.bin");
        FileTransferSessionLease lease = registry.Begin(
            [new FileTransferRegistration(source, destination)],
            isMove: true);

        var runner = new BoundedStaOperationRunner(
            maxConcurrency: 1,
            maxQueued: 1,
            queueTimeout: TimeSpan.FromSeconds(5),
            threadName: "DeskBox.TestLeaseRetention",
            idleKeepAlive: TimeSpan.Zero);
        var watchdog = new StaOperationWatchdog(
            inactivityTimeout: TimeSpan.FromMilliseconds(40),
            pollInterval: TimeSpan.FromMilliseconds(10));

        var staEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWedgedWorker = new ManualResetEventSlim(false);
        var workerUnwound = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await Assert.ThrowsAsync<StaOperationAbandonedException>(() =>
                FileService.RunStaTransferWithLeaseRetentionAsync(
                    runner,
                    watchdog,
                    lease,
                    () =>
                    {
                        staEntered.TrySetResult(true);
                        releaseWedgedWorker.Wait(TimeSpan.FromSeconds(10));
                        workerUnwound.TrySetResult(true);
                        return 42;
                    },
                    CancellationToken.None));

            await staEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Critical safety invariant (FL-11): while the abandoned STA worker is still
            // executing IFileOperation, the session lease MUST stay active so concurrent retries
            // are blocked by HasTransferConflict.
            Assert.Equal(1, registry.ActiveSessionCount);
            Assert.True(registry.IsPathActive(source));
            Assert.True(registry.IsPathActive(destination));

            // Once the wedged STA worker finally unwinds, its finally disposes the lease.
            releaseWedgedWorker.Set();
            await workerUnwound.Task.WaitAsync(TimeSpan.FromSeconds(5));

            for (int i = 0; i < 40 && registry.ActiveSessionCount > 0; i++)
            {
                await Task.Delay(15);
            }

            Assert.Equal(0, registry.ActiveSessionCount);
            Assert.False(registry.IsPathActive(source));
        }
        finally
        {
            releaseWedgedWorker.Set();
            lease.Dispose();
        }
    }

    [Fact]
    public void ShellDeleteProgressSink_PauseTimerPausesWatchdogAndResumeTimerRestoresIt()
    {
        long now = 0;
        var watchdog = new StaOperationWatchdog(TimeSpan.FromSeconds(45), () => now);
        var sink = new FileService.ShellDeleteProgressSink(
            [],
            interactive: true,
            allowUndo: true,
            watchdog);

        sink.PauseTimer();
        now = 3_600_000; // User left UAC / delete confirmation dialog open for 1 hour.
        Assert.False(watchdog.ShouldAbandon(now, null, out _));

        sink.ResumeTimer();
        now += 44_999;
        Assert.False(watchdog.ShouldAbandon(now, null, out _));
        now += 1;
        Assert.True(watchdog.ShouldAbandon(now, null, out _));
    }

    [Fact]
    public void ResolveDirectChildDestinationPath_FiltersNestedPartialMergeLeaves()
    {
        string fenceRoot = Path.Combine(_sandbox, "Fence");
        string directFile = Path.Combine(fenceRoot, "report.pdf");
        string directSubfolder = Path.Combine(fenceRoot, "Project");
        string nestedLeaf = Path.Combine(fenceRoot, "Project", "Docs", "spec.md");
        string externalFile = Path.Combine(_sandbox, "OtherFence", "report.pdf");

        Assert.Equal(
            Path.GetFullPath(directFile),
            WidgetViewModel.ResolveDirectChildDestinationPath(directFile, fenceRoot),
            ignoreCase: true);
        Assert.Equal(
            Path.GetFullPath(directSubfolder),
            WidgetViewModel.ResolveDirectChildDestinationPath(directSubfolder, fenceRoot),
            ignoreCase: true);
        Assert.Null(WidgetViewModel.ResolveDirectChildDestinationPath(nestedLeaf, fenceRoot));
        Assert.Null(WidgetViewModel.ResolveDirectChildDestinationPath(externalFile, fenceRoot));
    }
}
