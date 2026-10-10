using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;

namespace DeskBox.Features.Backup;

public enum BackupPageMessageKind
{
    None, PasswordSaved, PasswordMissing, PasswordSaveFailed,
    ProbeSucceeded, ProbeFailed, ListFailed, NotYetVisible
}

public sealed record BackupPageMessage(BackupPageMessageKind Kind, string? Error = null)
{
    public static BackupPageMessage Empty { get; } = new(BackupPageMessageKind.None);
}

/// <summary>Owns one visible cloud settings visit and its endpoint-scoped reads.</summary>
public sealed partial class BackupSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IBackupSettings _settings;
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Action<Exception> _reportError;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string, string> _localize;
    private readonly Func<string, object[], string> _format;
    private CancellationTokenSource? _visit;
    private CancellationTokenSource? _endpointCancellation;
    private CancellationTokenSource? _commandCancellation;
    private Task<IReadOnlyList<BackupRemoteSnapshot>?>? _listInFlight;
    // Aggregates every in-flight read that holds the current scope's tokens
    // (credential checks, snapshot lists, post-upload verification delays).
    // A canceled scope is retired against this drain instead of being
    // disposed immediately: disposing a source a pending read still uses
    // throws ObjectDisposedException from token members such as WaitHandle.
    private Task _readDrain = Task.CompletedTask;
    private int _visitGeneration;
    private int _endpointGeneration;
    private int _commandGeneration;
    private bool _disposed;
    private bool _isBusy;
    private bool _credentialSaved;
    private BackupSettingsSnapshot _state;
    private BackupPageMessage _message = BackupPageMessage.Empty;
    private IReadOnlyList<BackupRemoteSnapshot> _remoteSnapshots = [];

    public BackupSettingsViewModel(IBackupSettings settings, Func<Action, bool> tryEnqueue,
        Action<Exception> reportError,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, string>? localize = null,
        Func<string, object[], string>? format = null,
        bool isStoreChannel = false)
    {
        _settings = settings;
        _tryEnqueue = tryEnqueue;
        _reportError = reportError;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _localize = localize ?? (key => key);
        _format = format ?? ((key, args) => key);
        ShowStoreUninstallNotice = isStoreChannel;
        _state = settings.Read();
        // The section binding surface (batch 49) projects the initial read;
        // the visit state machine below stays as batch 4 built it.
        InitializeSurface();
    }

    private string Localize(string key) => _localize(key);

    private string Format(string key, params object[] args) => _format(key, args);

    public BackupSettingsSnapshot State { get => _state; private set => SetProperty(ref _state, value); }
    public BackupPageMessage Message { get => _message; private set => SetProperty(ref _message, value); }
    public IReadOnlyList<BackupRemoteSnapshot> RemoteSnapshots
        { get => _remoteSnapshots; private set => SetProperty(ref _remoteSnapshots, value); }
    public bool CredentialSaved { get => _credentialSaved; private set => SetProperty(ref _credentialSaved, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool IsActive => !_disposed && _visit is not null;
    public int VisitGeneration => _visitGeneration;
    public int EndpointGeneration => _endpointGeneration;
    public BackupEndpoint Endpoint => State.Endpoint;

    /// <summary>
    /// Store-channel disclosure: a Store-installed DeskBox keeps its local
    /// backup snapshots inside the package's app-data folder, which Windows
    /// clears on uninstall, so those builds surface the data-loss notice
    /// above the cloud-backup section. Direct-installer builds keep local
    /// snapshots in a folder that survives reinstalling, so the notice stays
    /// hidden there. The channel arrives as a constructor primitive so the
    /// feature stays free of adapter dependencies.
    /// </summary>
    public bool ShowStoreUninstallNotice { get; }

    public void Activate()
    {
        if (_disposed || IsActive) return;
        _visit = new();
        ++_visitGeneration;
        _endpointCancellation = CancellationTokenSource.CreateLinkedTokenSource(_visit.Token);
        _settings.UploadCompleted += OnUploadCompleted;
        RefreshState(recheckCredential: false);
        _ = RefreshCredentialAsync(listAfter: true);
    }

    public void Deactivate()
    {
        if (_visit is null) return;
        ++_visitGeneration;
        _settings.UploadCompleted -= OnUploadCompleted;
        _visit.Cancel();
        _endpointCancellation?.Cancel();
        _commandCancellation?.Cancel();
        _commandCancellation = null;
        // Hidden-visit reads keep running on the canceled tokens, so the
        // scope must be retired (kept referenced, disposed only once its
        // drain settles) rather than disposed in place.
        RetireCancellationScope(_visit, _endpointCancellation, _readDrain);
        _readDrain = Task.CompletedTask;
        _visit = null;
        _endpointCancellation = null;
        // The retired read keeps its own reference; clearing the active slot
        // only stops the next visit from deduplicating against it.
        _listInFlight = null;
        IsBusy = false;
        CredentialSaved = false;
        Message = BackupPageMessage.Empty;
        RemoteSnapshots = [];
    }

    /// <summary>
    /// Releases a canceled cancellation scope. The sources must outlive the
    /// reads that still hold their tokens (Dispose-before-drain makes token
    /// members such as <see cref="CancellationToken.WaitHandle"/> throw
    /// ObjectDisposedException), so the disposal is deferred to the drain
    /// task that aggregates those reads. A drain that never settles (test
    /// gates, teardown racing a hung transport) leaves the canceled sources
    /// to the process-level backstop instead of disposing them unsafely.
    /// </summary>
    private static void RetireCancellationScope(
        CancellationTokenSource? visit, CancellationTokenSource? endpoint, Task drain)
    {
        if (drain.IsCompleted)
        {
            endpoint?.Dispose();
            visit?.Dispose();
            return;
        }

        _ = drain.ContinueWith(_ =>
        {
            endpoint?.Dispose();
            visit?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Adds one background read to the drain that retirement waits on before
    /// releasing the current scope's cancellation sources. Every launch site
    /// that hands <see cref="EndpointToken"/> to fire-and-forget work must
    /// route through here so the drain stays complete.
    /// </summary>
    private void TrackRead(Task read) => _readDrain = Task.WhenAll(_readDrain, read);

    public void RefreshState() => RefreshState(recheckCredential: true);

    private void RefreshState(bool recheckCredential)
    {
        if (_disposed) return;
        BackupEndpoint before = State.Endpoint;
        State = _settings.Read();
        if (before == State.Endpoint) return;
        InvalidateEndpoint(recheckCredential);
    }

    public void Update(BackupSettingsChange change)
    {
        if (_disposed) return;
        _settings.Update(change);
        RefreshState();
    }

    public bool IsValidLocalDirectory(string path, out string? reason) =>
        _settings.IsValidLocalDirectory(path, out reason);

    private void InvalidateEndpoint(bool recheckCredential)
    {
        ++_endpointGeneration;
        _endpointCancellation?.Cancel();
        // The superseded endpoint's reads keep running on its canceled token:
        // retire the source against its drain instead of disposing it inline.
        RetireCancellationScope(null, _endpointCancellation, _readDrain);
        _readDrain = Task.CompletedTask;
        _endpointCancellation = IsActive
            ? CancellationTokenSource.CreateLinkedTokenSource(_visit!.Token) : null;
        _commandCancellation?.Cancel();
        _commandCancellation = null;
        _listInFlight = null;
        IsBusy = false;
        CredentialSaved = false;
        Message = BackupPageMessage.Empty;
        RemoteSnapshots = [];
        if (IsActive && recheckCredential) _ = RefreshCredentialAsync(listAfter: false);
    }

    private CancellationToken EndpointToken =>
        _endpointCancellation?.Token ?? new CancellationToken(canceled: true);

    private bool IsCurrent(int visit, int endpoint, CancellationToken token) =>
        IsActive && visit == _visitGeneration && endpoint == _endpointGeneration &&
        !token.IsCancellationRequested;

    /// <summary>
    /// Re-checks the endpoint credential (and optionally lists snapshots).
    /// The entry point routes the read through <see cref="TrackRead"/> so the
    /// cancellation scope retirement waits for it no matter who launched it.
    /// </summary>
    public Task RefreshCredentialAsync(bool listAfter = false)
    {
        Task read = RefreshCredentialCoreAsync(listAfter);
        TrackRead(read);
        return read;
    }

    private async Task RefreshCredentialCoreAsync(bool listAfter)
    {
        if (!IsActive) return;
        int visit = _visitGeneration;
        int endpointGeneration = _endpointGeneration;
        BackupEndpoint endpoint = Endpoint;
        CancellationToken token = EndpointToken;
        try
        {
            bool saved = State.HasEndpoint && await _settings.HasCredentialAsync(endpoint, token);
            if (!IsCurrent(visit, endpointGeneration, token)) return;
            CredentialSaved = saved;
            if (saved && listAfter) await RefreshSnapshotsCoreAsync(checkCredential: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrent(visit, endpointGeneration, token)) _reportError(ex);
        }
    }

    public Task<bool> SaveCredentialAsync(string secret) => RunCommandAsync(async (endpoint, token) =>
    {
        await _settings.SaveAsync();
        token.ThrowIfCancellationRequested();
        await _settings.SaveCredentialAsync(endpoint, secret, token);
    }, BackupPageMessageKind.PasswordSaved, BackupPageMessageKind.PasswordSaveFailed,
        afterSuccess: () =>
        {
            CredentialSaved = true;
            _ = RefreshSnapshotsAsync();
        });

    public Task<bool> ProbeAsync(string? secretOverride) => RunCommandAsync(async (endpoint, token) =>
    {
        await _settings.SaveAsync();
        token.ThrowIfCancellationRequested();
        await _settings.ProbeAsync(endpoint, secretOverride, token);
    }, BackupPageMessageKind.ProbeSucceeded, BackupPageMessageKind.ProbeFailed);

    private async Task<bool> RunCommandAsync(Func<BackupEndpoint, CancellationToken, Task> action,
        BackupPageMessageKind success, BackupPageMessageKind failure, Action? afterSuccess = null)
    {
        if (!IsActive || IsBusy) return false;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(EndpointToken);
        _commandCancellation = cancellation;
        int command = ++_commandGeneration;
        int visit = _visitGeneration;
        int endpointGeneration = _endpointGeneration;
        BackupEndpoint endpoint = Endpoint;
        CancellationToken token = cancellation.Token;
        bool Current() => command == _commandGeneration && IsCurrent(visit, endpointGeneration, token);
        IsBusy = true;
        Message = BackupPageMessage.Empty;
        try
        {
            await action(endpoint, token);
            if (!Current()) return false;
            Message = new(success);
            afterSuccess?.Invoke();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (Current())
            {
                Message = new(failure, ex.Message);
                _reportError(ex);
            }
            return false;
        }
        finally
        {
            if (Current()) IsBusy = false;
            if (ReferenceEquals(_commandCancellation, cancellation)) _commandCancellation = null;
            cancellation.Dispose();
        }
    }

    public Task<IReadOnlyList<BackupRemoteSnapshot>?> RefreshSnapshotsAsync() =>
        RefreshSnapshotsCoreAsync(checkCredential: true);

    private Task<IReadOnlyList<BackupRemoteSnapshot>?> RefreshSnapshotsCoreAsync(bool checkCredential)
    {
        if (!IsActive) return Task.FromResult<IReadOnlyList<BackupRemoteSnapshot>?>(null);
        if (_listInFlight is { IsCompleted: false } pending) return pending;
        Task<IReadOnlyList<BackupRemoteSnapshot>?> task = ListCoreAsync(checkCredential);
        TrackRead(task);
        _listInFlight = task.IsCompleted ? null : task;
        return task;
    }

    private async Task<IReadOnlyList<BackupRemoteSnapshot>?> ListCoreAsync(bool checkCredential)
    {
        int visit = _visitGeneration;
        int endpointGeneration = _endpointGeneration;
        BackupEndpoint endpoint = Endpoint;
        CancellationToken token = EndpointToken;
        try
        {
            if (!State.HasEndpoint) return null;
            if (checkCredential)
            {
                bool saved = await _settings.HasCredentialAsync(endpoint, token);
                if (!IsCurrent(visit, endpointGeneration, token)) return null;
                CredentialSaved = saved;
                if (!saved)
                {
                    Message = new(BackupPageMessageKind.PasswordMissing);
                    RemoteSnapshots = [];
                    return null;
                }
            }
            IReadOnlyList<BackupRemoteSnapshot> snapshots = await _settings.ListAsync(endpoint, token);
            if (!IsCurrent(visit, endpointGeneration, token)) return null;
            RemoteSnapshots = snapshots;
            Message = BackupPageMessage.Empty;
            return snapshots;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            if (IsCurrent(visit, endpointGeneration, token))
            {
                Message = new(BackupPageMessageKind.ListFailed, ex.Message);
                _reportError(ex);
            }
            return null;
        }
        finally
        {
            if (_listInFlight is { IsCompleted: false } running &&
                endpointGeneration == _endpointGeneration && visit == _visitGeneration &&
                ReferenceEquals(running, _listInFlight))
                _listInFlight = null;
        }
    }

    public void RemoveSnapshot(string name)
    {
        if (!IsActive) return;
        RemoteSnapshots = RemoteSnapshots.Where(item => item.Name != name).ToArray();
        Message = BackupPageMessage.Empty;
        TrackRead(RefreshAfterDelayAsync());
    }

    private async Task RefreshAfterDelayAsync()
    {
        int visit = _visitGeneration;
        int generation = _endpointGeneration;
        CancellationToken token = EndpointToken;
        try
        {
            await _delay(TimeSpan.FromMilliseconds(1500), token);
            if (IsCurrent(visit, generation, token)) await RefreshSnapshotsAsync();
        }
        catch (OperationCanceledException) { }
    }

    private void OnUploadCompleted(BackupUploadNotification notification)
    {
        if (!IsActive) return;
        int visit = _visitGeneration;
        int generation = _endpointGeneration;
        _tryEnqueue(() =>
        {
            if (!IsActive || visit != _visitGeneration || generation != _endpointGeneration) return;
            RefreshState();
            if (notification.Endpoint != Endpoint || !notification.Uploaded ||
                notification.RemoteFilePath is not { } path) return;
            int slash = path.LastIndexOf('/');
            TrackRead(VerifyUploadedSnapshotAsync(path[(slash + 1)..]));
        });
    }

    private async Task VerifyUploadedSnapshotAsync(string expectedName)
    {
        int visit = _visitGeneration;
        int generation = _endpointGeneration;
        CancellationToken token = EndpointToken;
        bool listed = false;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await _delay(TimeSpan.FromMilliseconds(attempt == 0 ? 1500 : 3000), token);
                if (!IsCurrent(visit, generation, token)) return;
                IReadOnlyList<BackupRemoteSnapshot>? snapshots = await RefreshSnapshotsAsync();
                if (!IsCurrent(visit, generation, token)) return;
                if (snapshots is null) continue;
                listed = true;
                if (snapshots.Any(item => string.Equals(item.Name, expectedName, StringComparison.Ordinal))) return;
            }
            if (listed && IsCurrent(visit, generation, token))
                Message = new(BackupPageMessageKind.NotYetVisible);
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Deactivate();
        _disposed = true;
    }
}
