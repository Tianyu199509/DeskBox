using DeskBox.Contracts;
using DeskBox.Features.Search;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchSettingsCoordinatorTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly List<SearchSettingsCoordinator> _coordinators = [];

    public SearchSettingsCoordinatorTests() => _settings = new SettingsService(_root);
    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DisabledFeature_DoesNotInitializeOrProbeRuntime()
    {
        int starts = 0;
        var connection = new FakeConnection();
        var coordinator = Create(() => null, () => { starts++; return connection; });
        Assert.False(coordinator.Read().FeatureEnabled);
        Assert.Equal(EverythingConnectionState.Unknown, (await coordinator.RefreshConnectionAsync(default)).State);
        coordinator.UpdatePreferences(new(EverythingEnabled: true));
        Assert.Equal(0, starts);
        Assert.Equal(0, connection.Probes);
    }

    [Fact]
    public async Task RepeatedVisits_BorrowOneRuntime_WithoutDisposingIt()
    {
        FeatureWidgetSettings.SetEnabled(_settings.Settings, WidgetKind.Search, true);
        int starts = 0;
        FakeConnection? current = null;
        var coordinator = Create(() => current, () => { starts++; return current = new FakeConnection(); });
        using var editor = new SearchSettingsViewModel(coordinator, action => { action(); return true; }, _ => { });
        for (int i = 0; i < 3; i++)
        {
            editor.Activate();
            await editor.PendingOperation;
            editor.Deactivate();
        }
        Assert.Equal(1, starts);
        Assert.Equal(3, current!.Probes);
        Assert.Equal(1, current.Subscribers);
        Assert.Equal(0, current.Disposals);
        coordinator.Dispose();
        Assert.Equal(0, current.Subscribers);
        Assert.Equal(0, current.Disposals);
    }

    [Fact]
    public async Task RuntimeReplacement_CancelsOldRequestsAndUnsubscribesOldProvider()
    {
        FeatureWidgetSettings.SetEnabled(_settings.Settings, WidgetKind.Search, true);
        var completion = new TaskCompletionSource<EverythingConnectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new FakeConnection { Probe = _ => completion.Task };
        var replacement = new FakeConnection();
        FakeConnection? current = old;
        var coordinator = Create(() => current, () => current);
        coordinator.OnRuntimeChanged();
        Task<EverythingConnectionSnapshot> request = coordinator.RefreshConnectionAsync(default);
        Action<EverythingConnectionSnapshot>? oldHandler = old.CaptureNotification();
        int notifications = 0;
        coordinator.StateChanged += () => notifications++;

        coordinator.OnRuntimeStopping();
        Assert.True(old.LastToken.IsCancellationRequested);
        Assert.Equal(0, old.Subscribers);
        current = replacement;
        coordinator.OnRuntimeChanged();
        int afterReplacement = notifications;
        oldHandler?.Invoke(EverythingConnectionSnapshot.Unknown);
        Assert.Equal(afterReplacement, notifications);
        completion.SetResult(EverythingConnectionSnapshot.Unknown);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await coordinator.RefreshConnectionAsync(default);
        Assert.Equal(1, replacement.Probes);
        Assert.Equal(1, replacement.Subscribers);
    }

    [Fact]
    public async Task DisableClosesWidgetThenDrainsProbeBeforeRuntimeRelease()
    {
        FeatureWidgetSettings.SetEnabled(_settings.Settings, WidgetKind.Search, true);
        var pending = new TaskCompletionSource<EverythingConnectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeConnection { Probe = _ => pending.Task };
        var order = new List<string>();
        SearchSettingsCoordinator? coordinator = null;
        coordinator = Create(() => connection, () => connection,
            applyWidget: async (enabled, _) =>
            {
                order.Add($"window:{enabled}");
                await coordinator!.CommitEnabledStateAsync(enabled);
            },
            setRuntime: enabled => order.Add($"runtime:{enabled}"));
        coordinator.OnRuntimeChanged();
        Task<EverythingConnectionSnapshot> probe = coordinator.RefreshConnectionAsync(default);

        Task disable = coordinator.SetEnabledAsync(false);
        Assert.True(connection.LastToken.IsCancellationRequested);
        Assert.Contains("window:False", order);
        Assert.DoesNotContain("runtime:False", order);
        Assert.False(disable.IsCompleted);
        pending.SetResult(EverythingConnectionSnapshot.Unknown);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        await disable;
        Assert.Equal("runtime:False", order.Last());
        Assert.False(coordinator.Enabled);
    }

    [Fact]
    public async Task RapidEnableDisableDoesNotLetOldWindowReenableSearch()
    {
        var pendingWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        SearchSettingsCoordinator? coordinator = null;
        coordinator = Create(() => null, () => null,
            applyWidget: async (enabled, _) =>
            {
                order.Add($"window:{enabled}");
                if (enabled) await pendingWindow.Task;
                await coordinator!.CommitEnabledStateAsync(enabled);
            },
            setRuntime: enabled => order.Add($"runtime:{enabled}"));
        Task first = coordinator.SetEnabledAsync(true);
        Task last = coordinator.SetEnabledAsync(false);
        Assert.False(coordinator.Enabled);
        pendingWindow.SetResult();
        await Task.WhenAll(first, last);
        Assert.False(coordinator.Enabled);
        Assert.Equal("runtime:False", order.Last());
        Assert.Equal(1, order.Count(item => item == "runtime:True"));
    }

    [Fact]
    public async Task ExternalSettingChangeReconcilesWindowAndFailureCanRetry()
    {
        int runtimeStarts = 0;
        var disabledApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Create(() => null, () => null,
            applyWidget: (enabled, _) =>
            {
                if (!enabled) disabledApplied.TrySetResult();
                return Task.CompletedTask;
            },
            setRuntime: enabled =>
            {
                if (enabled && ++runtimeStarts == 1) throw new IOException("runtime unavailable");
            });
        await Assert.ThrowsAsync<IOException>(() => coordinator.SetEnabledAsync(true));
        Assert.True(coordinator.Enabled);
        await coordinator.SetEnabledAsync(true);
        Assert.Equal(2, runtimeStarts);

        FeatureWidgetSettings.SetEnabled(_settings.Settings, WidgetKind.Search, false);
        await _settings.SaveAsync();
        await disabledApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(coordinator.Enabled);
    }

    [Fact]
    public async Task StopCancelsAndDrainsProbeBeforeDisposal()
    {
        FeatureWidgetSettings.SetEnabled(_settings.Settings, WidgetKind.Search, true);
        var pending = new TaskCompletionSource<EverythingConnectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeConnection { Probe = _ => pending.Task };
        var coordinator = Create(() => connection, () => connection);
        Task<EverythingConnectionSnapshot> probe = coordinator.RefreshConnectionAsync(default);
        Task stop = coordinator.StopAsync();
        Assert.True(connection.LastToken.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        pending.SetResult(EverythingConnectionSnapshot.Unknown);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        await stop;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.SetEnabledAsync(true));
    }

    [Fact]
    public async Task HungProbe_SkipsBorrowedRuntimeDisposalDuringShutdown()
    {
        FeatureWidgetSettings.SetEnabled(_settings.Settings, WidgetKind.Search, true);
        var pending = new TaskCompletionSource<EverythingConnectionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeConnection { Probe = _ => pending.Task };
        var coordinator = Create(() => connection, () => connection);
        Task<EverythingConnectionSnapshot> probe = coordinator.RefreshConnectionAsync(default);
        var shutdown = new ShutdownSequence(_ => { });

        bool completed = await shutdown.RunAsync(
            ShutdownStep.Bounded("search-settings", coordinator.StopAsync,
                TimeSpan.FromMilliseconds(30), abortFollowingStepsOnTimeout: true),
            ShutdownStep.Sync("search-runtime", connection.Dispose));

        Assert.False(completed);
        Assert.True(connection.LastToken.IsCancellationRequested);
        Assert.Equal(0, connection.Disposals);
        pending.SetResult(EverythingConnectionSnapshot.Unknown);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        await coordinator.StopAsync();
        Assert.Equal(0, connection.Disposals);
    }

    [Fact]
    public async Task Preferences_OnlyUpdateSearchSliceAndApplyLiveContentScopeOnce()
    {
        _settings.Settings.Language = "zh-TW";
        _settings.Settings.Todo.TodoDefaultReminderOffsetMinutes = 30;
        var scopes = new List<bool>();
        var coordinator = Create(() => null, () => null, scopes: scopes.Add);
        coordinator.UpdatePreferences(new(IncludeDeskBoxContent: false, DefaultTab: "APP", IconAnimation: 2));
        coordinator.UpdatePreferences(new(IncludeDeskBoxContent: false));
        Assert.Equal(new[] { false }, scopes);
        Assert.Equal("app", coordinator.Read().Preferences.DefaultTab);
        Assert.Equal("zh-TW", _settings.Settings.Language);
        Assert.Equal(30, _settings.Settings.Todo.TodoDefaultReminderOffsetMinutes);
        await _settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.False(reloaded.Settings.Search.SearchIncludeDeskBoxContent);
        Assert.Equal(2, reloaded.Settings.Search.SearchAppIconAnimation);
    }

    [Fact]
    public void HotkeyFailure_IsReportedWithoutOverwritingThePreviousGesture()
    {
        FeatureWidgetSettings.SetEnabled(_settings.Settings, WidgetKind.Search, true);
        var hotkey = new FakeHotkey(_settings) { RejectGesture = true };
        var coordinator = Create(() => null, () => null, () => hotkey);
        GlobalHotkeyGesture before = coordinator.Read().Hotkey.Gesture;
        SearchHotkeyUpdateResult result = coordinator.ApplyHotkey(SearchSettingsViewModel.AltSpaceGesture);
        Assert.False(result.Succeeded);
        Assert.Equal("already-owned", result.Error);
        Assert.Equal(before, coordinator.Read().Hotkey.Gesture);

        SearchHotkeyUpdateResult enabled = coordinator.SetHotkeyEnabled(true);
        Assert.False(enabled.Succeeded);
        Assert.NotNull(enabled.Error);
        Assert.True(coordinator.Read().Hotkey.Enabled);
        Assert.False(coordinator.Read().Hotkey.Registered);
    }

    [Fact]
    public async Task CancelledNativeSettingsCommands_DoNotMutatePathOrPublishCheckingState()
    {
        _settings.Settings.Search.SearchEverythingExecutablePath = "keep.exe";
        using var connection = new EverythingSearchService(_settings);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int events = 0;
        connection.ConnectionChanged += _ => events++;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.RefreshConnectionAsync(false, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.UseAutomaticDetectionAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SetExecutablePathAsync("new.exe", cancellation.Token));
        Assert.Equal("keep.exe", _settings.Settings.Search.SearchEverythingExecutablePath);
        Assert.Equal(0, events);
    }

    private SearchSettingsCoordinator Create(
        Func<ISearchConnectionClient?> get, Func<ISearchConnectionClient?> ensure,
        Func<ISearchHotkeyController?>? hotkey = null, Action<bool>? scopes = null,
        Func<bool, bool, Task>? applyWidget = null, Action<bool>? setRuntime = null)
    {
        var coordinator = new SearchSettingsCoordinator(_settings, TestServices.CreateLocalizationService(),
            get, ensure, hotkey ?? (() => null), scopes ?? (_ => { }),
            applyWidget, setRuntime);
        _coordinators.Add(coordinator);
        return coordinator;
    }

    public async Task DisposeAsync()
    {
        foreach (var coordinator in _coordinators) coordinator.Dispose();
        await _settings.FlushPendingSaveAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeConnection : ISearchConnectionClient, IDisposable
    {
        private Action<EverythingConnectionSnapshot>? _changed;
        public int Probes { get; private set; }
        public int Subscribers { get; private set; }
        public int Disposals { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public EverythingConnectionSnapshot CurrentSnapshot { get; private set; } = EverythingConnectionSnapshot.Unknown;
        public Func<CancellationToken, Task<EverythingConnectionSnapshot>> Probe { get; init; } = _ => Task.FromResult(EverythingConnectionSnapshot.Unknown);
        public event Action<EverythingConnectionSnapshot>? ConnectionChanged
        {
            add { _changed += value; Subscribers++; }
            remove { _changed -= value; Subscribers--; }
        }
        public Action<EverythingConnectionSnapshot>? CaptureNotification() => _changed;
        public async Task<EverythingConnectionSnapshot> RefreshConnectionAsync(bool allowIpcProbe, CancellationToken cancellationToken)
        {
            Probes++;
            LastToken = cancellationToken;
            return CurrentSnapshot = await Probe(cancellationToken);
        }
        public async Task UseAutomaticDetectionAsync(CancellationToken cancellationToken) => await RefreshConnectionAsync(false, cancellationToken);
        public Task<bool> SetExecutablePathAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> LaunchEverythingAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public void Dispose() => Disposals++;
    }

    private sealed class FakeHotkey(SettingsService settings) : ISearchHotkeyController
    {
        public bool RejectGesture { get; init; }
        public bool IsRegistered => false;
        public GlobalHotkeyGesture CurrentGesture => new(
            (HotkeyModifierKeys)settings.Settings.Search.SearchHotkeyModifiers, settings.Settings.Search.SearchHotkeyKey);
        public void SetEnabled(bool enabled) => settings.Settings.Search.SearchHotkeyEnabled = enabled;
        public bool TryApplyGesture(GlobalHotkeyGesture gesture, out string? error)
        {
            error = RejectGesture ? "already-owned" : null;
            if (RejectGesture) return false;
            settings.Settings.Search.SearchHotkeyModifiers = (int)gesture.Modifiers;
            settings.Settings.Search.SearchHotkeyKey = gesture.VirtualKey;
            return true;
        }
    }
}
