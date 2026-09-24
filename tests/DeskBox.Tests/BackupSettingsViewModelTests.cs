using DeskBox.Contracts;
using DeskBox.Features.Backup;

namespace DeskBox.Tests;

public sealed class BackupSettingsViewModelTests
{
    private static readonly BackupEndpoint A = new("webdav", "https://a.example/dav", "DeskBox/backups", "simon");
    private static readonly BackupEndpoint B = new("webdav", "https://b.example/dav", "DeskBox/backups", "simon");

    [Fact]
    public async Task EndpointEdit_CancelsAndRejectsLateCredentialAndListResults()
    {
        var oldCredential = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldList = new TaskCompletionSource<IReadOnlyList<BackupRemoteSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeSettings(A)
        {
            Credential = (endpoint, _) => endpoint == A ? oldCredential.Task : Task.FromResult(true),
            List = (endpoint, _) => endpoint == A ? oldList.Task : Task.FromResult<IReadOnlyList<BackupRemoteSnapshot>>(
                [new("new.zip", 10, DateTimeOffset.UtcNow)])
        };
        using var editor = Create(fake);
        editor.Activate();
        Task oldCheck = editor.RefreshCredentialAsync();
        CancellationToken oldToken = fake.LastCredentialToken;
        fake.SetEndpoint(B);
        editor.RefreshState();
        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal(B, editor.Endpoint);
        Assert.True(editor.CredentialSaved);
        oldCredential.SetResult(false);
        await oldCheck;
        Assert.True(editor.CredentialSaved);
        Assert.Empty(editor.RemoteSnapshots);

        fake.SetEndpoint(A);
        fake.Credential = (_, _) => Task.FromResult(true);
        editor.RefreshState();
        Task<IReadOnlyList<BackupRemoteSnapshot>?> stale = editor.RefreshSnapshotsAsync();
        fake.SetEndpoint(B);
        editor.RefreshState();
        Task<IReadOnlyList<BackupRemoteSnapshot>?> fresh = editor.RefreshSnapshotsAsync();
        Assert.Single((await fresh)!);
        oldList.SetResult([new("old.zip", 1, DateTimeOffset.UtcNow)]);
        Assert.Null(await stale);
        Assert.Equal("new.zip", Assert.Single(editor.RemoteSnapshots).Name);
    }

    [Fact]
    public async Task HiddenVisit_CancelsReadAndRejectsLateResultAfterReopen()
    {
        var late = new TaskCompletionSource<IReadOnlyList<BackupRemoteSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int listCalls = 0;
        var fake = new FakeSettings(A)
        {
            Credential = (_, _) => Task.FromResult(true),
            List = (_, _) => ++listCalls == 1 ? late.Task :
                Task.FromResult<IReadOnlyList<BackupRemoteSnapshot>>([new("fresh.zip", 2, null)])
        };
        using var editor = Create(fake);
        editor.Activate();
        Task<IReadOnlyList<BackupRemoteSnapshot>?> old = editor.RefreshSnapshotsAsync();
        CancellationToken oldToken = fake.LastListToken;
        editor.Deactivate();
        Assert.True(oldToken.IsCancellationRequested);
        editor.Activate();
        Task<IReadOnlyList<BackupRemoteSnapshot>?> fresh = editor.RefreshSnapshotsAsync();
        Assert.Equal("fresh.zip", Assert.Single((await fresh)!).Name);
        late.SetResult([new("stale.zip", 3, null)]);
        Assert.Null(await old);
        Assert.Equal("fresh.zip", Assert.Single(editor.RemoteSnapshots).Name);
    }

    [Fact]
    public async Task ProbeFailureCanRetry_AndOldProbeCannotClearNewStatus()
    {
        var oldProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeSettings(A)
        {
            Probe = (endpoint, _, _) => endpoint == A ? oldProbe.Task : Task.CompletedTask
        };
        var errors = new List<Exception>();
        using var editor = new BackupSettingsViewModel(fake, action => { action(); return true; }, errors.Add);
        editor.Activate();
        Task<bool> old = editor.ProbeAsync(null);
        Assert.True(editor.IsBusy);
        fake.SetEndpoint(B);
        editor.RefreshState();
        Assert.False(editor.IsBusy);
        Assert.True(await editor.ProbeAsync(null));
        Assert.Equal(BackupPageMessageKind.ProbeSucceeded, editor.Message.Kind);
        oldProbe.SetException(new IOException("old endpoint offline"));
        Assert.False(await old);
        Assert.Equal(BackupPageMessageKind.ProbeSucceeded, editor.Message.Kind);
        Assert.Empty(errors);

        fake.Probe = (_, _, _) => Task.FromException(new IOException("offline"));
        Assert.False(await editor.ProbeAsync(null));
        Assert.Equal(BackupPageMessageKind.ProbeFailed, editor.Message.Kind);
        fake.Probe = (_, _, _) => Task.CompletedTask;
        Assert.True(await editor.ProbeAsync(null));
        Assert.Equal(BackupPageMessageKind.ProbeSucceeded, editor.Message.Kind);
    }

    [Fact]
    public async Task QueuedOldUploadAndHiddenPageDoNotStartRemoteReads()
    {
        var queue = new Queue<Action>();
        var fake = new FakeSettings(A);
        using var editor = new BackupSettingsViewModel(fake,
            action => { queue.Enqueue(action); return true; }, _ => { },
            (_, _) => Task.CompletedTask);
        editor.Activate();
        fake.Raise(new(A, true, "DeskBox/backups/old.zip"));
        fake.SetEndpoint(B);
        editor.RefreshState();
        while (queue.TryDequeue(out Action? callback)) callback();
        Assert.Equal(0, fake.ListCalls);
        editor.Deactivate();
        fake.Raise(new(B, true, "DeskBox/backups/hidden.zip"));
        Assert.Empty(queue);
        Assert.Equal(0, fake.ListCalls);
        await editor.RefreshSnapshotsAsync();
        Assert.Equal(0, fake.ListCalls);
    }

    [Fact]
    public void VisibleMatchingUploadRefreshesList_AndRepeatedVisitsKeepOneSubscription()
    {
        bool saved = false;
        var fake = new FakeSettings(A)
        {
            Credential = (_, _) => Task.FromResult(saved),
            List = (_, _) => Task.FromResult<IReadOnlyList<BackupRemoteSnapshot>>(
                [new("uploaded.zip", 12, null)])
        };
        using var editor = new BackupSettingsViewModel(fake,
            action => { action(); return true; }, _ => { },
            (_, _) => Task.CompletedTask);
        editor.Activate();
        editor.Activate();
        Assert.Equal(1, fake.Subscribers);
        saved = true;
        fake.Raise(new(A, true, "DeskBox/backups/uploaded.zip"));
        Assert.Equal("uploaded.zip", Assert.Single(editor.RemoteSnapshots).Name);
        Assert.Equal(1, fake.ListCalls);
        editor.Deactivate();
        Assert.Equal(0, fake.Subscribers);
        editor.Activate();
        Assert.Equal(1, fake.Subscribers);
        editor.Deactivate();
        Assert.Equal(0, fake.Subscribers);
    }

    [Fact]
    public async Task SaveCredentialFailureCanRetry_SecretStaysOutOfSettingsState()
    {
        int attempts = 0;
        var fake = new FakeSettings(A)
        {
            Credential = (_, _) => Task.FromResult(attempts >= 2),
            SaveCredential = (_, _, _) => ++attempts == 1
                ? Task.FromException(new IOException("vault unavailable"))
                : Task.CompletedTask
        };
        using var editor = new BackupSettingsViewModel(fake, action => { action(); return true; }, _ => { });
        editor.Activate();
        Assert.False(await editor.SaveCredentialAsync("secret-once"));
        Assert.Equal(BackupPageMessageKind.PasswordSaveFailed, editor.Message.Kind);
        Assert.False(editor.IsBusy);
        Assert.True(await editor.SaveCredentialAsync("secret-twice"));
        Assert.True(editor.CredentialSaved);
        Assert.DoesNotContain("secret", editor.State.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, attempts);
    }

    private static BackupSettingsViewModel Create(FakeSettings fake) =>
        new(fake, action => { action(); return true; }, _ => { });

    private sealed class FakeSettings(BackupEndpoint endpoint) : IBackupSettings
    {
        public BackupSettingsSnapshot Snapshot { get; private set; } = MakeSnapshot(endpoint);
        public Func<BackupEndpoint, CancellationToken, Task<bool>> Credential { get; set; } =
            (_, _) => Task.FromResult(false);
        public Func<BackupEndpoint, CancellationToken, Task<IReadOnlyList<BackupRemoteSnapshot>>> List { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<BackupRemoteSnapshot>>([]);
        public Func<BackupEndpoint, string?, CancellationToken, Task> Probe { get; set; } =
            (_, _, _) => Task.CompletedTask;
        public Func<BackupEndpoint, string, CancellationToken, Task> SaveCredential { get; set; } =
            (_, _, _) => Task.CompletedTask;
        public CancellationToken LastCredentialToken { get; private set; }
        public CancellationToken LastListToken { get; private set; }
        public int ListCalls { get; private set; }
        private Action<BackupUploadNotification>? _uploadCompleted;
        public int Subscribers { get; private set; }
        public event Action<BackupUploadNotification>? UploadCompleted
        {
            add { _uploadCompleted += value; Subscribers++; }
            remove { _uploadCompleted -= value; Subscribers--; }
        }
        public void Raise(BackupUploadNotification info) => _uploadCompleted?.Invoke(info);
        public void SetEndpoint(BackupEndpoint value) => Snapshot = MakeSnapshot(value);
        public BackupSettingsSnapshot Read() => Snapshot;
        public void Update(BackupSettingsChange change) { }
        public bool IsValidLocalDirectory(string path, out string? rejectionReasonKey)
            { rejectionReasonKey = null; return true; }
        public Task SaveAsync() => Task.CompletedTask;
        public Task<bool> HasCredentialAsync(BackupEndpoint endpoint, CancellationToken token)
            { LastCredentialToken = token; return Credential(endpoint, token); }
        public Task SaveCredentialAsync(BackupEndpoint endpoint, string secret, CancellationToken token) =>
            SaveCredential(endpoint, secret, token);
        public Task ProbeAsync(BackupEndpoint endpoint, string? secret, CancellationToken token) =>
            Probe(endpoint, secret, token);
        public Task<IReadOnlyList<BackupRemoteSnapshot>> ListAsync(BackupEndpoint endpoint, CancellationToken token)
            { LastListToken = token; ListCalls++; return List(endpoint, token); }

        private static BackupSettingsSnapshot MakeSnapshot(BackupEndpoint endpoint) => new(
            true, 1440, 7, string.Empty, @"C:\backup\automatic", @"C:\backup", false,
            endpoint.Provider, endpoint.ServerUrl, endpoint.RemotePath, endpoint.Username,
            true, false, false, 1440, 5, 0, 0, 0, endpoint, true);
    }
}
