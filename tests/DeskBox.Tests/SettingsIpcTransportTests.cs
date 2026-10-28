using System.IO.Pipes;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Batch 3b transport tests for the settings-host IPC pipe: request/response
/// round-trip, settings-commit end-to-end (host sink → pipe → main apply),
/// event push, and disconnect faulting of pending requests.
/// Host and main services intentionally live in DIFFERENT directories here:
/// within one test process they would share the per-directory commit gate
/// and the synchronous pipe round-trip would deadlock — the shipped bridge
/// is cross-process, where the gates live in separate processes.
/// </summary>
public sealed class SettingsIpcTransportTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _hostDir;
    private readonly string _mainDir;

    public SettingsIpcTransportTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _hostDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "host")).FullName;
        _mainDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "main")).FullName;
    }

    private static string PipeName => "deskbox-tests-settings-ipc-" + Guid.NewGuid().ToString("N");

    private static async Task<SettingsIpcServer> StartServerAsync(
        string pipeName,
        SettingsService? mainService)
    {
        var server = SettingsIpcServer.Start(pipeName, async (method, payload, _) =>
        {
            if (method == SettingsIpcMethods.Ping)
            {
                return (true, "pong", null);
            }

            if (method == SettingsIpcMethods.SettingsCommit && mainService is not null)
            {
                return await SettingsIpcDispatch.SettingsCommitAsync(mainService, payload);
            }

            return (false, null, $"unknown method {method}");
        });

        // The client's own retry loop waits out the accept loop's bind.
        await using (var client = await SettingsIpcClient.ConnectAsync(
                   pipeName,
                   TimeSpan.FromMilliseconds(200),
                   attempts: 25))
        {
            (bool ok, string? payload, _) = await client.RequestAsync(SettingsIpcMethods.Ping);
            Assert.True(ok);
            Assert.Equal("pong", payload);
        }

        return server;
    }

    [Fact]
    public async Task RequestResponse_RoundTripsOverThePipe()
    {
        string pipe = PipeName;
        await using var server = await StartServerAsync(pipe, null);
        await using var client = await SettingsIpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(2));

        for (int i = 0; i < 5; i++)
        {
            (bool ok, string? payload, string? error) = await client.RequestAsync(
                SettingsIpcMethods.Ping,
                "tick " + i);
            Assert.True(ok, error);
            Assert.Equal("pong", payload);
        }

        (bool unknownOk, _, string? unknownError) = await client.RequestAsync("nope", null);
        Assert.False(unknownOk);
        Assert.Contains("unknown method", unknownError);
    }

    [Fact]
    public async Task SettingsCommit_TravelsHostSinkToMainApply()
    {
        var main = new SettingsService(_mainDir);
        await main.LoadAsync();
        // GlobalHotkeyEnabled defaults to true; the host commits the
        // opposite so the apply is observable.
        Assert.True(main.Settings.GlobalHotkeyEnabled);

        string pipe = PipeName;
        await using var server = await StartServerAsync(pipe, main);

        await using var client = await SettingsIpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(2));
        var host = new SettingsService(_hostDir, new RemoteSettingsPersistenceSink(client));
        host.Settings.GlobalHotkeyEnabled = false;

        Assert.True(await host.SaveCheckedAsync());
        Assert.False(main.Settings.GlobalHotkeyEnabled);

        // Durable on the main side.
        var fresh = new SettingsService(_mainDir);
        await fresh.LoadAsync();
        Assert.False(fresh.Settings.GlobalHotkeyEnabled);
        // The host side wrote nothing locally.
        Assert.False(File.Exists(Path.Combine(_hostDir, "settings.json")));
    }

    [Fact]
    public async Task EventPush_ReachesClientHandlers()
    {
        string pipe = PipeName;
        await using var server = await StartServerAsync(pipe, null);
        await using var client = await SettingsIpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(2));

        var received = new TaskCompletionSource<(string Method, string? Payload)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (method, payload) => received.TrySetResult((method, payload));

        await server.SendEventAsync(SettingsIpcMethods.SettingsReloaded, "42");
        (string method, string? payload) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SettingsIpcMethods.SettingsReloaded, method);
        Assert.Equal("42", payload);
    }

    [Fact]
    public async Task ServerShutdown_FaultsPendingRequests()
    {
        string pipe = PipeName;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = SettingsIpcServer.Start(pipe, async (method, _, _) =>
        {
            if (method == "hang")
            {
                await gate.Task;
                return (true, null, null);
            }

            return (true, null, null);
        });

        await using var client = await SettingsIpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(2));
        Task<(bool, string?, string?)> pending = client.RequestAsync("hang", null);
        await Task.Delay(200); // let the request reach the dispatcher

        await server.DisposeAsync();
        gate.TrySetResult();

        await Assert.ThrowsAnyAsync<Exception>(() => pending);
    }

    [Fact]
    public async Task OrderedWriteCommands_ApplyInWireOrder()
    {
        // Deep review R13 (R12 debt ②, diseases C/F): a dropdown flipped
        // twice sends two write commands back-to-back; concurrent dispatch
        // could apply the second (fast) before the first (slow) and leave
        // the OLDER value standing. Ordered methods must apply in wire
        // order.
        string pipe = PipeName;
        var applied = new List<string>();
        var slowArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = SettingsIpcServer.Start(pipe, (method, payload, _) =>
        {
            if (method == SettingsIpcMethods.WidgetApplyGroupPresentation)
            {
                string marker = payload ?? string.Empty;
                if (marker == "slow")
                {
                    slowArrived.TrySetResult();
                    return RunSlowAsync();
                }

                lock (applied)
                {
                    applied.Add(marker);
                }
            }

            return Task.FromResult((true, (string?)"done", (string?)null));

            async Task<(bool Ok, string? Payload, string? Error)> RunSlowAsync()
            {
                await slowGate.Task;
                lock (applied)
                {
                    applied.Add("slow");
                }

                return (true, "done", null);
            }
        });

        await using var client = await SettingsIpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(2));

        // Wire order is pinned by waiting for the server to SEE the first
        // request before sending the second.
        Task<(bool, string?, string?)> first = client.RequestAsync(
            SettingsIpcMethods.WidgetApplyGroupPresentation, "slow");
        await slowArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<(bool, string?, string?)> second = client.RequestAsync(
            SettingsIpcMethods.WidgetApplyGroupPresentation, "fast");

        // Give the fast write time to reach the server: without the ordered
        // lane it would dispatch immediately and apply while slow is held.
        await Task.Delay(250);
        slowGate.TrySetResult();
        await Task.WhenAll(first, second);

        lock (applied)
        {
            Assert.Equal(new[] { "slow", "fast" }, applied.ToArray());
        }
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
