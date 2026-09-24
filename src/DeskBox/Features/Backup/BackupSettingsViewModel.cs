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
public sealed class BackupSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IBackupSettings _settings;
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Action<Exception> _reportError;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _visit;
    private CancellationTokenSource? _endpointCancellation;
    private CancellationTokenSource? _commandCancellation;
    private Task<IReadOnlyList<BackupRemoteSnapshot>?>? _listInFlight;
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
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _settings = settings;
        _tryEnqueue = tryEnqueue;
        _reportError = reportError;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _state = settings.Read();
    }

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
        _listInFlight = null;
        _endpointCancellation?.Dispose();
        _endpointCancellation = null;
        _visit.Dispose();
        _visit = null;
        IsBusy = false;
        CredentialSaved = false;
        Message = BackupPageMessage.Empty;
        RemoteSnapshots = [];
    }

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
        _endpointCancellation?.Dispose();
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

    public async Task RefreshCredentialAsync(bool listAfter = false)
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
        _ = RefreshAfterDelayAsync();
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
            _ = VerifyUploadedSnapshotAsync(path[(slash + 1)..]);
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
