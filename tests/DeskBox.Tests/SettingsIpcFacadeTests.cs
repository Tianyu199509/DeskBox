using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Batch 3c-1: the remote facade pattern end-to-end — the settings-host
/// proxies (RemoteSettingsHotkeyActions / RemoteSettingsDataActions) over the
/// pipe against the main-side router wired to fake in-process facades, plus
/// the reload-push payload format that carries the echo-suppression host
/// commit id.
/// </summary>
public sealed class SettingsIpcFacadeTests : IDisposable
{
    private readonly string _tempRoot;

    public SettingsIpcFacadeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    private sealed class FakeHotkeyActions : ISettingsHotkeyActions
    {
        public GlobalHotkeyRuntimeState? StateToReturn;
        public GlobalHotkeyActivation? AppliedActivation;
        public bool ApplyResult = true;
        public bool ResetResult = true;
        public bool RefreshCalled;
        public bool? SetEnabledArg;
        public bool GestureOwnedResult;
        public bool GestureOwnedArg;
        public bool DoubleClickArg;
        public bool SuspendCalled;
        public bool ResumeCalled;

        public event Action? RegistrationChanged;

        public void RaiseRegistrationChanged() => RegistrationChanged?.Invoke();

        public GlobalHotkeyRuntimeState? GetRuntimeState() => StateToReturn;

        public bool TryApplyActivation(GlobalHotkeyActivation activation, out string? applyError)
        {
            AppliedActivation = activation;
            applyError = ApplyResult ? null : "apply failed";
            return ApplyResult;
        }

        public bool ResetToDefault(out string? error)
        {
            error = ResetResult ? null : "reset failed";
            return ResetResult;
        }

        public void RefreshRegistration() => RefreshCalled = true;

        public void SetEnabled(bool enabled) => SetEnabledArg = enabled;

        public bool IsGestureOwnedBySearchHotkey(GlobalHotkeyGesture gesture)
        {
            GestureOwnedArg = true;
            return GestureOwnedResult;
        }

        public DesktopDoubleClickToggleResult TrySetDesktopDoubleClickEnabled(bool enabled)
        {
            DoubleClickArg = enabled;
            return new DesktopDoubleClickToggleResult(!enabled, 42);
        }

        public void SuspendHotkeysForRecording() => SuspendCalled = true;

        public void ResumeHotkeysAfterRecording() => ResumeCalled = true;
    }

    private sealed class FakeDataActions : ISettingsDataActions
    {
        public (int Active, int Recent) Counts = (7, 3);
        public bool Cleared;
        public bool ClearedRecent;
        public bool CleanupCalled;
        public QuickCaptureImageCacheInfo? InfoToReturn = new(4, 1024, 1, 256);

        public Task<(int ActiveCount, int RecentCount)> GetQuickCaptureCountsAsync() =>
            Task.FromResult(Counts);

        public Task ClearQuickCaptureDataAsync()
        {
            Cleared = true;
            return Task.CompletedTask;
        }

        public Task ClearQuickCaptureRecentAsync()
        {
            ClearedRecent = true;
            return Task.CompletedTask;
        }

        public Task<QuickCaptureImageCacheCleanupResult> ClearUnusedQuickCaptureImageCacheAsync()
        {
            CleanupCalled = true;
            return Task.FromResult(new QuickCaptureImageCacheCleanupResult(1, 256));
        }

        public Task<QuickCaptureImageCacheInfo?> GetQuickCaptureImageCacheInfoAsync() =>
            Task.FromResult(InfoToReturn);
    }

    private static async Task<(SettingsIpcServer Server, FakeHotkeyActions Hotkey, FakeDataActions Data, SettingsIpcClient Client)>
        StartLoopbackAsync(string pipeName, SettingsService settings)
    {
        var hotkey = new FakeHotkeyActions();
        var data = new FakeDataActions();
        var router = new SettingsIpcMethodRouter(settings, hotkey, data);
        var server = SettingsIpcServer.Start(pipeName, router.DispatchAsync);
        var client = await SettingsIpcClient.ConnectAsync(pipeName, TimeSpan.FromMilliseconds(200), attempts: 25);
        (bool ok, string? payload, _) = await client.RequestAsync(SettingsIpcMethods.Ping);
        Assert.True(ok, "loopback ping failed: " + payload);
        return (server, hotkey, data, client);
    }

    private static string PipeName() => "deskbox-tests-facade-ipc-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task HotkeyFacade_RoundTripsEveryCommand()
    {
        var settings = new SettingsService(_tempRoot);
        var (server, hotkey, _, client) = await StartLoopbackAsync(PipeName(), settings);
        var remote = new RemoteSettingsHotkeyActions(client);
        try
        {
            var expectedState = new GlobalHotkeyRuntimeState(
                true,
                null,
                "F7",
                // VK_F7 = 0x78
                GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(HotkeyModifierKeys.None, 0x78)));
            hotkey.StateToReturn = expectedState;
            Assert.Equal(expectedState, remote.GetRuntimeState());

            var activation = GlobalHotkeyActivation.FromChord(
                new GlobalHotkeyGesture(HotkeyModifierKeys.Alt | HotkeyModifierKeys.Control, 0x20));
            Assert.True(remote.TryApplyActivation(activation, out string? applyError));
            Assert.Null(applyError);
            Assert.Equal(activation, hotkey.AppliedActivation);

            hotkey.ResetResult = false;
            Assert.False(remote.ResetToDefault(out string? resetError));
            Assert.Equal("reset failed", resetError);

            remote.RefreshRegistration();
            Assert.True(hotkey.RefreshCalled);

            remote.SetEnabled(false);
            Assert.False(hotkey.SetEnabledArg);

            hotkey.GestureOwnedResult = true;
            Assert.True(remote.IsGestureOwnedBySearchHotkey(activation.Gesture));

            DesktopDoubleClickToggleResult toggle = remote.TrySetDesktopDoubleClickEnabled(true);
            Assert.True(hotkey.DoubleClickArg);
            Assert.False(toggle.Applied);
            Assert.Equal(42, toggle.ErrorCode);

            remote.SuspendHotkeysForRecording();
            remote.ResumeHotkeysAfterRecording();
            Assert.True(hotkey.SuspendCalled);
            Assert.True(hotkey.ResumeCalled);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task DataFacade_RoundTripsEveryCommand()
    {
        var settings = new SettingsService(_tempRoot);
        var (server, _, data, client) = await StartLoopbackAsync(PipeName(), settings);
        var remote = new RemoteSettingsDataActions(client);
        try
        {
            (int active, int recent) = await remote.GetQuickCaptureCountsAsync();
            Assert.Equal((7, 3), (active, recent));

            await remote.ClearQuickCaptureDataAsync();
            Assert.True(data.Cleared);

            await remote.ClearQuickCaptureRecentAsync();
            Assert.True(data.ClearedRecent);

            QuickCaptureImageCacheCleanupResult cleanup = await remote.ClearUnusedQuickCaptureImageCacheAsync();
            Assert.True(data.CleanupCalled);
            Assert.Equal(1, cleanup.DeletedFileCount);
            Assert.Equal(256, cleanup.DeletedBytes);

            QuickCaptureImageCacheInfo? info = await remote.GetQuickCaptureImageCacheInfoAsync();
            Assert.NotNull(info);
            Assert.Equal(4, info!.TotalFileCount);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task HotkeyRegistrationEvent_ReachesRemoteSubscribers()
    {
        var settings = new SettingsService(_tempRoot);
        var (server, _, _, client) = await StartLoopbackAsync(PipeName(), settings);
        var remote = new RemoteSettingsHotkeyActions(client);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.RegistrationChanged += () => raised.TrySetResult();
        try
        {
            await server.SendEventAsync(SettingsIpcMethods.HotkeyRegistrationChanged, null);
            await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void ReloadPayload_ComposesAndParsesWithEchoSuppression()
    {
        string withHost = SettingsIpcReloadPayload.Compose(11, 7);
        Assert.True(SettingsIpcReloadPayload.TryParse(withHost, out long mainId, out long? hostId));
        Assert.Equal(11, mainId);
        Assert.Equal(7, hostId);

        string withoutHost = SettingsIpcReloadPayload.Compose(12, null);
        Assert.True(SettingsIpcReloadPayload.TryParse(withoutHost, out long mainId2, out long? hostId2));
        Assert.Equal(12, mainId2);
        Assert.Null(hostId2);

        Assert.False(SettingsIpcReloadPayload.TryParse(null, out _, out _));
        Assert.False(SettingsIpcReloadPayload.TryParse("garbage", out _, out _));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
