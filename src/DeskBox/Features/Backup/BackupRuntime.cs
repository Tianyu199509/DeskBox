using DeskBox.Contracts;

namespace DeskBox.Features.Backup;

/// <summary>
/// Owns scheduling, queued/manual work and shutdown. Storage and upload commit
/// points remain in the backend: a completed commit wins over late cancellation.
/// Lifecycle and settings changes are invoked on the timer's owning UI thread.
/// </summary>
public sealed class BackupRuntime : IBackupCommands, IAsyncDisposable
{
    private readonly IBackupBackend _backend;
    private readonly IBackupTimer _timer;
    private readonly Action<BackupCompletion> _report;
    private readonly object _sync = new();
    private readonly SemaphoreSlim[] _lanes = [new(1, 1), new(1, 1)];
    private readonly int[] _laneCounts = [0, 0];
    private readonly HashSet<Task> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool _started;
    private bool _stopping;
    private bool _refreshingOptions;
    private Task? _stopTask;

    public BackupRuntime(IBackupBackend backend, IBackupTimer timer, Action<BackupCompletion> report)
    {
        _backend = backend;
        _timer = timer;
        _report = report;
    }

    public bool IsStopping { get { lock (_sync) return _stopping; } }
    public int PendingCount { get { lock (_sync) return _pending.Count; } }
    public Task LastScheduledCheck { get; private set; } = Task.CompletedTask;

    public void Start()
    {
        if (IsStopping || _started) return;
        _timer.Tick += OnTick;
        try
        {
            _timer.Start();
            _started = true;
        }
        catch
        {
            _timer.Tick -= OnTick;
            try { _timer.Stop(); } catch { }
            throw;
        }
    }

    public void RefreshOptions(bool checkSchedule = false)
    {
        if (IsStopping || _refreshingOptions) return;
        _refreshingOptions = true;
        try { _backend.RefreshOptions(); }
        finally { _refreshingOptions = false; }
        if (checkSchedule) OnTick();
    }

    private void OnTick() => LastScheduledCheck = CheckScheduleAsync();

    private async Task CheckScheduleAsync()
    {
        if (IsStopping) return;
        Task local = _backend.LocalScheduleEnabled ? RunScheduledLocalAsync() : Task.CompletedTask;
        Task cloud = _backend.CloudScheduleEnabled ? RunScheduledCloudAsync() : Task.CompletedTask;
        try { await Task.WhenAll(local, cloud); }
        catch { /* Each tracked operation reports its own outcome. */ }
    }

    public Task<LocalBackupResult> RunScheduledLocalAsync() => RunAsync(
        BackupWorkKind.Local, scheduled: true, queueWhenBusy: false,
        token => _backend.CreateSnapshotAsync(false, token),
        new LocalBackupResult(BackupOutcome.AlreadyRunning), result => result.Outcome, default);

    private Task<CloudBackupRunResult> RunScheduledCloudAsync() => RunAsync(
        BackupWorkKind.Cloud, scheduled: true, queueWhenBusy: false,
        token => _backend.UploadAsync(true, token), CloudBackupRunResult.InProgress, result => result.Outcome, default);

    public Task<LocalBackupResult> CreateSnapshotNowAsync(CancellationToken cancellationToken = default) => RunAsync(
        BackupWorkKind.Local, scheduled: false, queueWhenBusy: true,
        token => _backend.CreateSnapshotAsync(true, token),
        new LocalBackupResult(BackupOutcome.AlreadyRunning), result => result.Outcome, cancellationToken);

    public Task<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken = default) => RunAsync(
        BackupWorkKind.Local, scheduled: false, queueWhenBusy: true,
        token => _backend.ExportAsync(destinationDirectory, token), string.Empty, _ => BackupOutcome.Succeeded, cancellationToken);

    public Task<CloudBackupRunResult> UploadNowAsync(CancellationToken cancellationToken = default) => RunAsync(
        BackupWorkKind.Cloud, scheduled: false, queueWhenBusy: false,
        token => _backend.UploadAsync(false, token), CloudBackupRunResult.InProgress, result => result.Outcome, cancellationToken);

    private Task<T> RunAsync<T>(BackupWorkKind kind, bool scheduled, bool queueWhenBusy,
        Func<CancellationToken, Task<T>> work, T busyResult, Func<T, BackupOutcome> classify, CancellationToken cancellationToken)
    {
        TaskCompletionSource<T> completion;
        CancellationTokenSource request;
        int lane = (int)kind;
        lock (_sync)
        {
            if (_stopping || cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<T>(new CancellationToken(canceled: true));
            if (!queueWhenBusy && _laneCounts[lane] > 0) return Task.FromResult(busyResult);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _laneCounts[lane]++;
            _pending.Add(completion.Task);
        }
        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            using (request)
            {
                T result = default!;
                Exception? error = null;
                bool acquired = false;
                BackupOutcome outcome;
                try
                {
                    await _lanes[lane].WaitAsync(request.Token);
                    acquired = true;
                    if (!scheduled) await _backend.PrepareManualBackupAsync(request.Token);
                    result = await work(request.Token);
                    outcome = classify(result);
                }
                catch (OperationCanceledException ex) when (request.IsCancellationRequested)
                { error = ex; outcome = BackupOutcome.Canceled; }
                catch (Exception ex)
                { error = ex; outcome = BackupOutcome.Failed; }
                finally
                { if (acquired) _lanes[lane].Release(); }

                Report(new(kind, scheduled, outcome, error?.Message));
                lock (_sync)
                {
                    _laneCounts[lane]--;
                    _pending.Remove(completion.Task);
                    if (outcome == BackupOutcome.Canceled) completion.TrySetCanceled(request.Token);
                    else if (error is not null) completion.TrySetException(error);
                    else completion.TrySetResult(result);
                }
            }
        }
    }

    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        Task[] pending;
        lock (_sync)
        {
            _stopping = true;
            pending = _pending.ToArray();
        }
        _timer.Tick -= OnTick;
        try { _timer.Stop(); }
        catch (Exception ex) { Report(new(BackupWorkKind.Local, true, BackupOutcome.Failed, ex.Message)); }
        try { _timer.Dispose(); }
        catch (Exception ex) { Report(new(BackupWorkKind.Local, true, BackupOutcome.Failed, ex.Message)); }
        try { _lifetime.Cancel(); }
        catch (Exception ex) { Report(new(BackupWorkKind.Local, true, BackupOutcome.Failed, ex.Message)); }
        // A failed or canceled operation must not interrupt the remaining teardown.
        try { await Task.WhenAll(pending); } catch { }
        _lifetime.Dispose();
        foreach (SemaphoreSlim lane in _lanes) lane.Dispose();
    }

    private void Report(BackupCompletion completion)
    {
        try { _report(completion); } catch { }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
