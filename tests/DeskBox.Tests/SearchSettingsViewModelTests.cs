using DeskBox.Contracts;
using DeskBox.Features.Search;
using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class SearchSettingsViewModelTests
{
    [Fact]
    public async Task Visit_IsIdempotentAndDoesNotOwnTheSharedRuntime()
    {
        var settings = new FakeSettings();
        using var editor = CreateEditor(settings);
        Assert.Equal(0, settings.RefreshCount);
        editor.Activate();
        editor.Activate();
        await editor.PendingOperation;
        Assert.Equal(1, settings.RefreshCount);
        Assert.Equal(1, settings.Subscribers);
        CancellationToken visit = editor.VisitToken;
        editor.Deactivate();
        Assert.True(visit.IsCancellationRequested);
        Assert.Equal(0, settings.Subscribers);
        editor.Activate();
        await editor.PendingOperation;
        Assert.Equal(2, settings.RefreshCount);
        editor.Dispose();
        Assert.Equal(0, settings.Subscribers);
        Assert.Equal(0, settings.Disposals);
    }

    [Fact]
    public async Task SupersededProbe_CannotOverwriteNewResultEvenIfBackendIgnoresCancellation()
    {
        var pending = new TaskCompletionSource<EverythingConnectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new FakeSettings();
        settings.Probe = (_, call) => call == 1 ? pending.Task : Task.FromResult(Connected("new"));
        using var editor = CreateEditor(settings);
        editor.Activate();
        Task oldProbe = editor.PendingOperation;
        CancellationToken oldToken = settings.LastToken;
        await editor.RefreshAsync();
        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal("new", editor.Connection.Version);
        pending.SetResult(Connected("old"));
        await oldProbe;
        Assert.Equal("new", editor.Connection.Version);
        Assert.False(editor.IsBusy);
    }

    [Fact]
    public async Task HiddenVisit_RejectsQueuedNotificationsAndLateProbeCompletion()
    {
        var pending = new TaskCompletionSource<EverythingConnectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new Queue<Action>();
        var settings = new FakeSettings { Probe = (_, _) => pending.Task };
        using var editor = new SearchSettingsViewModel(settings, action => { queue.Enqueue(action); return true; }, _ => { });
        editor.Activate();
        Task probe = editor.PendingOperation;
        Action? oldNotification = settings.CaptureNotification();
        settings.Raise(Connected("queued"));
        editor.Deactivate();
        while (queue.TryDequeue(out Action? action)) action();
        pending.SetResult(Connected("late"));
        await probe;
        Assert.Equal(EverythingConnectionState.Unknown, editor.Connection.State);
        Assert.False(editor.IsBusy);

        settings.Probe = (_, _) => Task.FromResult(Connected("return"));
        editor.Activate();
        await editor.PendingOperation;
        oldNotification?.Invoke();
        settings.CurrentConnection = Connected("stale-event");
        while (queue.TryDequeue(out Action? action)) action();
        Assert.Equal("return", editor.Connection.Version);
    }

    [Fact]
    public async Task ProbeFailure_CanRecoverOnExplicitRetry()
    {
        var errors = new List<Exception>();
        var settings = new FakeSettings
        {
            Probe = (_, call) => call == 1
                ? Task.FromException<EverythingConnectionSnapshot>(new IOException("offline"))
                : Task.FromResult(Connected("recovered"))
        };
        using var editor = new SearchSettingsViewModel(settings, action => { action(); return true; }, errors.Add);
        editor.Activate();
        await editor.PendingOperation;
        Assert.Equal(SearchSettingsFailure.Connection, editor.Failure);
        Assert.False(editor.IsBusy);
        Assert.Single(errors);
        await editor.RefreshAsync();
        Assert.Equal(SearchSettingsFailure.None, editor.Failure);
        Assert.Equal("recovered", editor.Connection.Version);
    }

    [Fact]
    public async Task FeatureDisable_CancelsVisitOperationAndLateInputDoesNotWriteSettings()
    {
        var pending = new TaskCompletionSource<EverythingConnectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new FakeSettings { Probe = (_, _) => pending.Task };
        using var editor = CreateEditor(settings);
        editor.Activate();
        Task probe = editor.PendingOperation;
        CancellationToken token = settings.LastToken;
        settings.Snapshot = settings.Snapshot with { FeatureEnabled = false };
        settings.Raise(EverythingConnectionSnapshot.Unknown);
        Assert.True(token.IsCancellationRequested);
        Assert.False(editor.State.FeatureEnabled);
        Assert.False(editor.IsBusy);
        editor.Deactivate();
        editor.UpdatePreferences(new(IncludeDeskBoxContent: false));
        editor.ApplyHotkey(SearchSettingsViewModel.AltSpaceGesture);
        await editor.SelectExecutableAsync("ignored.exe");
        Assert.Equal(0, settings.Writes);
        pending.SetResult(Connected("ignored"));
        await probe;
    }

    [Fact]
    public async Task ReservedGestureRequiresConfirmation_AndConflictKeepsCurrentGesture()
    {
        var settings = new FakeSettings { HotkeyResult = new(false, "conflict") };
        using var editor = CreateEditor(settings);
        editor.Activate();
        await editor.PendingOperation;
        GlobalHotkeyGesture previous = editor.State.Hotkey.Gesture;
        Assert.True(editor.RequiresReservedHotkeyConfirmation(SearchSettingsViewModel.AltSpaceGesture));
        Assert.False(editor.RequiresReservedHotkeyConfirmation(previous));
        editor.ApplyHotkey(SearchSettingsViewModel.AltSpaceGesture);
        Assert.Equal("conflict", editor.HotkeyError);
        Assert.Equal(previous, editor.State.Hotkey.Gesture);
        editor.ResetHotkey();
        Assert.Equal(SearchSettingsViewModel.DefaultGesture, settings.LastGesture);
    }

    private static SearchSettingsViewModel CreateEditor(FakeSettings settings) =>
        new(settings, action => { action(); return true; }, exception => throw new InvalidOperationException("Unexpected error", exception));

    private static EverythingConnectionSnapshot Connected(string version) =>
        new(EverythingConnectionState.Connected, "Everything.exe", version, true, false, null);

    private sealed class FakeSettings : ISearchSettings, IDisposable
    {
        private Action? _changed;
        public int Subscribers { get; private set; }
        public int RefreshCount { get; private set; }
        public int Writes { get; private set; }
        public int Disposals { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public GlobalHotkeyGesture LastGesture { get; private set; }
        public SearchHotkeyUpdateResult HotkeyResult { get; init; } = new(true);
        public Func<CancellationToken, int, Task<EverythingConnectionSnapshot>> Probe { get; set; } = (_, _) => Task.FromResult(Connected("1"));
        public SearchSettingsSnapshot Snapshot { get; set; } = new(true,
            new(true, false, true, true, "all", 0),
            new(true, true, true, SearchSettingsViewModel.DefaultGesture, "Alt + D"));
        public EverythingConnectionSnapshot CurrentConnection { get; set; } = EverythingConnectionSnapshot.Unknown;
        public EverythingConnectionSnapshot Connection => Snapshot.FeatureEnabled ? CurrentConnection : EverythingConnectionSnapshot.Unknown;
        public event Action? StateChanged
        {
            add { _changed += value; Subscribers++; }
            remove { _changed -= value; Subscribers--; }
        }
        public Action? CaptureNotification() => _changed;
        public void Raise(EverythingConnectionSnapshot snapshot) { CurrentConnection = snapshot; _changed?.Invoke(); }
        public SearchSettingsSnapshot Read() => Snapshot;
        public string FormatHotkey(GlobalHotkeyGesture gesture) => "Alt + Space";
        public void UpdatePreferences(SearchPreferenceChange change) => Writes++;
        public SearchHotkeyUpdateResult SetHotkeyEnabled(bool enabled) { Writes++; return HotkeyResult; }
        public SearchHotkeyUpdateResult ApplyHotkey(GlobalHotkeyGesture gesture) { Writes++; LastGesture = gesture; return HotkeyResult; }
        public async Task<EverythingConnectionSnapshot> RefreshConnectionAsync(CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            CurrentConnection = await Probe(cancellationToken, ++RefreshCount);
            return CurrentConnection;
        }
        public Task<EverythingConnectionSnapshot> DetectAutomaticallyAsync(CancellationToken cancellationToken) => RefreshConnectionAsync(cancellationToken);
        public Task<bool> SelectExecutableAsync(string path, CancellationToken cancellationToken) { Writes++; return Task.FromResult(true); }
        public Task<bool> LaunchEverythingAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public void Dispose() => Disposals++;
    }
}
