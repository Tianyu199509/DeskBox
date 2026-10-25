namespace DeskBox.Tests;

using System.IO.Pipes;
using DeskBox.Platform;

/// <summary>
/// Functional pin for the native settings-pipe creation path (deep review
/// R13, R12 debt ④): Win32Helper.TryCreateSettingsIpcPipe must produce a
/// pipe whose user-only DACL plus medium-integrity no-write-up label the
/// kernel accepted AT CREATION, and whose adopted NamedPipeServerStream
/// supports the full async read/write roundtrip the transport relies on.
/// The SDDL details this guards (the ME alias, not MW; no
/// FILE_FLAG_FIRST_PIPE_INSTANCE, which returns ERROR_INVALID_PARAMETER on
/// current builds) were each probe-verified the hard way.
/// </summary>
public sealed class SettingsIpcNativePipeCreationTests
{
    [Fact]
    public async Task NativePipe_SupportsEchoRoundtripOnAdoptedStream()
    {
        string pipeName = "deskbox-tests-native-pipe-" + Guid.NewGuid().ToString("N");
        using var serverHandle = Win32Helper.TryCreateSettingsIpcPipe(
            @"\\.\pipe\" + pipeName,
            bufferSize: 64 * 1024);
        Assert.NotNull(serverHandle);

        using var server = new NamedPipeServerStream(
            PipeDirection.InOut,
            isAsync: true,
            isConnected: false,
            safePipeHandle: serverHandle!);
        Task acceptTask = server.WaitForConnectionAsync();

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));

        var serverEcho = Task.Run(async () =>
        {
            using var reader = new StreamReader(server, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            string? line = await reader.ReadLineAsync();
            using var writer = new StreamWriter(server, System.Text.Encoding.UTF8) { AutoFlush = true };
            await writer.WriteLineAsync("echo:" + line);
        });

        // Raw bytes: wrapping the client stream in a writer would close it
        // on dispose and kill the read side below.
        await client.WriteAsync(System.Text.Encoding.UTF8.GetBytes("ping\n"));
        await client.FlushAsync();

        using var clientReader = new StreamReader(client, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        string? echoed;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            echoed = await clientReader.ReadLineAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            echoed = null;
        }

        await serverEcho.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("echo:ping", echoed);
    }
}
