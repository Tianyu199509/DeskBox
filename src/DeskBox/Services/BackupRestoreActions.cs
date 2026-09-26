using DeskBox.Contracts;

namespace DeskBox.Services;

/// <summary>Explicit boundary for destructive remote actions and the staged restore transaction.</summary>
public sealed class BackupRestoreActions
{
    private readonly object _operationGate = new();
    private readonly HashSet<Task> _pending = [];
    private readonly HashSet<string> _temporaryDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SettingsService _settings;
    private readonly CloudBackupService _cloud;
    private readonly DeskBoxDataBackupService _local;
    private readonly Func<Task> _shutdownForRestart;
    private readonly Func<AppRelaunchScheduleResult> _scheduleRelaunch;
    private Task? _stopTask;
    private bool _stopping;
    private bool _preparedScopedRestore;
    private bool _preservePendingRestore;

    internal BackupRestoreActions(SettingsService settings, CloudBackupService cloud,
        DeskBoxDataBackupService local, Func<Task> shutdownForRestart,
        Func<AppRelaunchScheduleResult>? scheduleRelaunch = null)
    {
        _settings = settings;
        _cloud = cloud;
        _local = local;
        _shutdownForRestart = shutdownForRestart;
        _scheduleRelaunch = scheduleRelaunch ?? AppRelaunchService.ScheduleAfterCurrentProcessExit;
    }

    public bool IsCurrentEndpoint(BackupEndpoint endpoint) =>
        CloudBackupSettingsPolicy.GetOptions(_settings.Settings).Endpoint == endpoint;

    private CloudBackupOptions CaptureCurrent(BackupEndpoint endpoint)
    {
        CloudBackupOptions options = CloudBackupSettingsPolicy.GetOptions(_settings.Settings);
        if (options.Endpoint != endpoint)
            throw new OperationCanceledException("The backup endpoint changed.");
        return options;
    }

    public Task DeleteSnapshotAsync(BackupEndpoint endpoint, string name,
        CancellationToken cancellationToken = default)
    {
        CloudBackupOptions options = CaptureCurrent(endpoint);
        return TrackAsync(async token =>
        {
            await _cloud.DeleteRemoteSnapshotAsync(options, name, token);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Remote inventory (PROPFIND) for the restore picker. Read-only, but it
    /// goes through the same endpoint capture and stop freeze as the
    /// destructive actions so a list read cannot outlive shutdown.
    /// </summary>
    internal Task<IReadOnlyList<CloudBackupRemoteEntry>> ListSnapshotsAsync(
        BackupEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        CloudBackupOptions options = CaptureCurrent(endpoint);
        return TrackAsync(token => _cloud.ListRemoteSnapshotsAsync(options, token), cancellationToken);
    }

    public Task<string> DownloadSnapshotAsync(BackupEndpoint endpoint, string name, string directory,
        CancellationToken cancellationToken = default)
    {
        CloudBackupOptions options = CaptureCurrent(endpoint);
        lock (_operationGate)
        {
            if (_stopping) return Task.FromCanceled<string>(new CancellationToken(true));
            _temporaryDownloads.Add(Path.GetFullPath(directory));
        }
        return TrackAsync(token => _cloud.DownloadSnapshotAsync(
            options, name, directory, token), cancellationToken);
    }

    public Task<DeskBoxRestorePreparation> PrepareScopedRestoreAsync(string archivePath,
        CloudBackupDomain scope, CancellationToken cancellationToken = default) =>
        TrackAsync(async token =>
        {
            DeskBoxRestorePreparation preparation =
                await _local.PrepareScopedRestoreAsync(archivePath, scope, token);
            lock (_operationGate) _preparedScopedRestore = true;
            return preparation;
        }, cancellationToken);

    public Task CancelPendingRestoreAsync(CancellationToken cancellationToken = default) =>
        TrackAsync(async token =>
        {
            await _local.CancelPendingRestoreAsync(token);
            lock (_operationGate) _preparedScopedRestore = false;
            return true;
        }, cancellationToken);

    public Task<bool> SetPendingRestoreItemReplaceModeAsync(bool replace,
        CancellationToken cancellationToken = default) =>
        TrackAsync(token => _local.SetPendingRestoreItemReplaceModeAsync(replace, token),
            cancellationToken);

    public AppRelaunchScheduleResult ScheduleRelaunch()
    {
        lock (_operationGate)
        {
            if (_stopping) return AppRelaunchScheduleResult.Failed("DeskBox is shutting down.");
            // Mark the handoff before the restart callback enters App shutdown.
            AppRelaunchScheduleResult result = _scheduleRelaunch();
            if (result.Started) _preservePendingRestore = true;
            return result;
        }
    }

    public Task ShutdownForRestartAsync() => _shutdownForRestart();

    public void DeleteTemporaryDownload(string? directory)
    {
        if (directory is null) return;
        CloudBackupService.TryDeleteDirectory(directory);
        if (!Directory.Exists(directory))
        {
            lock (_operationGate) _temporaryDownloads.Remove(Path.GetFullPath(directory));
        }
    }

    public Task StopAsync()
    {
        TaskCompletionSource completion;
        Task[] pending;
        lock (_operationGate)
        {
            if (_stopTask is not null) return _stopTask;
            _stopping = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            pending = _pending.ToArray();
        }
        _ = StopCoreAsync(pending, completion);
        return completion.Task;
    }

    private async Task StopCoreAsync(Task[] pending, TaskCompletionSource completion)
    {
        try
        {
            _lifetime.Cancel();
            try { await Task.WhenAll(pending); } catch { }

            bool cancelPendingRestore;
            string[] downloads;
            lock (_operationGate)
            {
                // A scoped restore prepared here is abandoned on ordinary
                // shutdown; a confirmed relaunch must keep its marker.
                cancelPendingRestore = _preparedScopedRestore && !_preservePendingRestore;
                downloads = _temporaryDownloads.ToArray();
                _temporaryDownloads.Clear();
            }
            try
            {
                if (cancelPendingRestore) await _local.CancelPendingRestoreAsync();
            }
            finally
            {
                foreach (string directory in downloads)
                    CloudBackupService.TryDeleteDirectory(directory);
            }
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private Task<T> TrackAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource linked;
        lock (_operationGate)
        {
            if (_stopping) return Task.FromCanceled<T>(new CancellationToken(true));
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token);
            // Register before starting work so StopAsync cannot miss a fast exit.
            _pending.Add(completion.Task);
        }
        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            try
            {
                completion.TrySetResult(await action(linked.Token));
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                lock (_operationGate) _pending.Remove(completion.Task);
                linked.Dispose();
            }
        }
    }
}
