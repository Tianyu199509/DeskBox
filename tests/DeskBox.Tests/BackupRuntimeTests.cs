using DeskBox.Contracts;
using DeskBox.Features.Backup;

namespace DeskBox.Tests;

public sealed class BackupRuntimeTests
{
    [Fact]
    public async Task TimerAndManualCloud_ShareOneLaneWithoutQueuingDuplicateUploads()
    {
        var release = new TaskCompletionSource<CloudBackupRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend { CloudEnabled = true, CloudWork = (_, _) => release.Task };
        var timer = new FakeTimer();
        var reports = new List<BackupCompletion>();
        await using var runtime = new BackupRuntime(backend, timer, reports.Add);
        runtime.Start();
        runtime.Start();
        timer.Fire();
        Task scheduled = runtime.LastScheduledCheck;
        Assert.Equal(1, timer.Starts);
        Assert.Equal(1, backend.CloudCalls);
        Assert.True((await runtime.UploadNowAsync()).AlreadyInProgress);
        timer.Fire();
        await runtime.LastScheduledCheck;
        Assert.Equal(1, backend.CloudCalls);
        release.SetResult(new(true, "remote.zip", 0));
        await scheduled;
        Assert.Equal(BackupOutcome.Succeeded, Assert.Single(reports).Outcome);
        Assert.Equal(0, runtime.PendingCount);
    }

    [Fact]
    public async Task ManualLocal_WaitsItsTurnAndScheduledTicksDoNotBuildABacklog()
    {
        var release = new TaskCompletionSource<LocalBackupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend { LocalEnabled = true };
        backend.LocalWork = (force, _) => force
            ? Task.FromResult(new LocalBackupResult(BackupOutcome.Succeeded, "manual.zip")) : release.Task;
        var timer = new FakeTimer();
        await using var runtime = new BackupRuntime(backend, timer, _ => { });
        Task<LocalBackupResult> scheduled = runtime.RunScheduledLocalAsync();
        Task<LocalBackupResult> manual = runtime.CreateSnapshotNowAsync();
        Assert.False(manual.IsCompleted);
        Assert.Equal(BackupOutcome.AlreadyRunning, (await runtime.RunScheduledLocalAsync()).Outcome);
        Assert.Equal(1, backend.LocalCalls);
        release.SetResult(new(BackupOutcome.Succeeded, "auto.zip"));
        await scheduled;
        Assert.Equal("manual.zip", (await manual).ArchivePath);
        Assert.Equal(2, backend.LocalCalls);
        Assert.Equal(1, backend.Preparations);
    }

    [Fact]
    public async Task Stop_CancelsPendingWorkAndWaitsForTheActiveCommit()
    {
        var release = new TaskCompletionSource<LocalBackupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken activeToken = default;
        var backend = new FakeBackend { LocalWork = (_, token) => { activeToken = token; return release.Task; } };
        var timer = new FakeTimer();
        var reports = new List<BackupCompletion>();
        var runtime = new BackupRuntime(backend, timer, reports.Add);
        runtime.Start();
        Task<LocalBackupResult> active = runtime.CreateSnapshotNowAsync();
        Task<LocalBackupResult> queued = runtime.CreateSnapshotNowAsync();
        Task stop = runtime.StopAsync();
        Assert.Same(stop, runtime.StopAsync());
        Assert.True(activeToken.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.UploadNowAsync());
        timer.Fire();
        // A backend that has already committed returns success despite a
        // late cancellation; the runtime must not overwrite that outcome.
        release.SetResult(new(BackupOutcome.Succeeded, "committed.zip"));
        Assert.Equal("committed.zip", (await active).ArchivePath);
        await stop;
        Assert.Equal(0, runtime.PendingCount);
        Assert.Equal(1, backend.LocalCalls);
        Assert.Contains(reports, item => item.Outcome == BackupOutcome.Canceled);
        Assert.Contains(reports, item => item.Outcome == BackupOutcome.Succeeded);
        Assert.Equal(1, timer.Disposals);
    }

    [Fact]
    public async Task ScheduledFailure_IsObservedAndLaterTicksCanRetry()
    {
        int attempt = 0;
        var backend = new FakeBackend
        {
            CloudEnabled = true,
            CloudWork = (_, _) => ++attempt == 1
                ? Task.FromException<CloudBackupRunResult>(new IOException("upload failed"))
                : Task.FromResult(new CloudBackupRunResult(true, "ok.zip", 0))
        };
        var timer = new FakeTimer();
        var reports = new List<BackupCompletion>();
        await using var runtime = new BackupRuntime(backend, timer, reports.Add);
        runtime.Start();
        timer.Fire();
        await runtime.LastScheduledCheck;
        timer.Fire();
        await runtime.LastScheduledCheck;
        Assert.Equal(new[] { BackupOutcome.Failed, BackupOutcome.Succeeded }, reports.Select(item => item.Outcome));
    }

    [Fact]
    public async Task SettingsRefresh_CoalescesReentrantNotificationsAndRespectsDisabledSchedules()
    {
        var backend = new FakeBackend();
        var timer = new FakeTimer();
        await using var runtime = new BackupRuntime(backend, timer, _ => { });
        backend.Refresh = () => runtime.RefreshOptions(checkSchedule: true);
        runtime.RefreshOptions(checkSchedule: true);
        await runtime.LastScheduledCheck;
        Assert.Equal(1, backend.Refreshes);
        Assert.Equal(0, backend.LocalCalls + backend.CloudCalls);
        backend.LocalEnabled = true;
        runtime.RefreshOptions(checkSchedule: true);
        await runtime.LastScheduledCheck;
        Assert.Equal(1, backend.LocalCalls);
    }

    [Fact]
    public async Task FlushFailure_PreventsManualBackupAndRemainsAFailure()
    {
        var backend = new FakeBackend { Prepare = _ => Task.FromException(new IOException("settings flush failed")) };
        var reports = new List<BackupCompletion>();
        await using var runtime = new BackupRuntime(backend, new FakeTimer(), reports.Add);
        await Assert.ThrowsAsync<IOException>(() => runtime.UploadNowAsync());
        Assert.Equal(0, backend.CloudCalls);
        Assert.Equal(BackupOutcome.Failed, Assert.Single(reports).Outcome);
    }

    [Fact]
    public async Task TimerStopFailure_DoesNotPreventCancellationAndDrain()
    {
        var backend = new FakeBackend { CloudWork = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(false, null, 0); } };
        var runtime = new BackupRuntime(backend, new FakeTimer { FailStop = true }, _ => { });
        Task<CloudBackupRunResult> work = runtime.UploadNowAsync();
        await runtime.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(0, runtime.PendingCount);
    }

    private sealed class FakeTimer : IBackupTimer
    {
        public event Action? Tick;
        public int Starts { get; private set; }
        public int Disposals { get; private set; }
        public bool FailStop { get; init; }
        public void Start() => Starts++;
        public void Stop() { if (FailStop) throw new InvalidOperationException("timer stop failed"); }
        public void Fire() => Tick?.Invoke();
        public void Dispose() => Disposals++;
    }

    private sealed class FakeBackend : IBackupBackend
    {
        public bool LocalEnabled { get; set; }
        public bool CloudEnabled { get; init; }
        public bool LocalScheduleEnabled => LocalEnabled;
        public bool CloudScheduleEnabled => CloudEnabled;
        public int Refreshes { get; private set; }
        public int Preparations { get; private set; }
        public int LocalCalls { get; private set; }
        public int CloudCalls { get; private set; }
        public Action? Refresh { get; set; }
        public Func<CancellationToken, Task> Prepare { get; init; } = _ => Task.CompletedTask;
        public Func<bool, CancellationToken, Task<LocalBackupResult>> LocalWork { get; set; } = (_, _) => Task.FromResult(new LocalBackupResult(BackupOutcome.Skipped));
        public Func<bool, CancellationToken, Task<CloudBackupRunResult>> CloudWork { get; init; } = (_, _) => Task.FromResult(new CloudBackupRunResult(false, null, 0));
        public void RefreshOptions() { Refreshes++; Refresh?.Invoke(); }
        public Task PrepareManualBackupAsync(CancellationToken token) { Preparations++; return Prepare(token); }
        public Task<LocalBackupResult> CreateSnapshotAsync(bool force, CancellationToken token) { LocalCalls++; return LocalWork(force, token); }
        public Task<string> ExportAsync(string path, CancellationToken token) => Task.FromResult(path);
        public Task<CloudBackupRunResult> UploadAsync(bool scheduled, CancellationToken token) { CloudCalls++; return CloudWork(scheduled, token); }
    }
}
