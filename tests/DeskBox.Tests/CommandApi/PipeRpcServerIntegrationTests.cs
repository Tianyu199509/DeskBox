using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeskBox.Cli;
using DeskBox.Protocol;
using DeskBox.Services.CommandApi;
using DeskBox.Services.CommandApi.Handlers;

namespace DeskBox.Tests.CommandApi;

/// <summary>
/// End-to-end transport tests: a real <see cref="PipeRpcServer"/> with ACLs
/// and framing, exercised through the real CLI client. No WinUI runtime is
/// involved, so these run on AnyCPU like the rest of the suite.
/// </summary>
public class PipeRpcServerIntegrationTests
{
    private readonly string _pipeName;

    public PipeRpcServerIntegrationTests()
    {
        _pipeName = $"DeskBox_Api_Pipe_{Guid.NewGuid():N}";
    }

    private PipeRpcServer CreateServer(StubUiDispatcher? uiDispatcher = null)
    {
        CommandRegistry registry = new(new ICommandHandler[]
        {
            new ServerPingHandler(),
            new WidgetsListHandler(() => null),
        });
        CommandDispatcher dispatcher = new(
            registry,
            () => false,
            () => false,
            uiDispatcher ?? new StubUiDispatcher(),
            serverVersion: "1.4.5",
            uiDispatchTimeout: TimeSpan.FromSeconds(2));
        PipeRpcServer server = new(dispatcher, _pipeName, auditLogFilePath: null, log: null);
        server.Start();
        return server;
    }

    [Fact]
    public async Task Server_PingThroughCliClient_ReturnsPongWithEnvelope()
    {
        PipeRpcServer server = CreateServer();
        try
        {
            PipeRpcClient client = new(_pipeName, timeoutMilliseconds: 5_000);

            JsonRpcResponse response = await client.SendAsync(
                "server/ping",
                JsonDocument.Parse("{}").RootElement.Clone(),
                clientVersion: "1.4.5").ConfigureAwait(true);

            Assert.Null(response.Error);
            Assert.NotNull(response.Result);
            Assert.True(response.Result!.Data!.Value.GetProperty("pong").GetBoolean());
            Assert.Equal(CommandApiProtocol.ProtocolVersion, response.Result.ProtocolVersion);
            Assert.Contains(CommandApiProtocol.Capabilities.ServerInfo, response.Result.Capabilities);
        }
        finally
        {
            await server.StopAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Server_GarbageFrame_GetsParseErrorBack()
    {
        PipeRpcServer server = CreateServer();
        try
        {
            using NamedPipeClientStream pipe = new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3_000).ConfigureAwait(true);

            byte[] garbage = Encoding.UTF8.GetBytes("this is not json");
            await CommandFrame.WriteAsync(pipe, garbage).ConfigureAwait(true);
            byte[] responsePayload = await CommandFrame.ReadAsync(pipe).ConfigureAwait(true);
            JsonRpcResponse? response = CommandApiJson.DeserializeResponse(responsePayload);

            Assert.NotNull(response);
            Assert.Equal(JsonRpcErrorCodes.ParseError, response!.Error!.Code);
            Assert.Equal(CommandApiProtocol.ErrorCodes.ParseError, response.Error.Data!.Code);
        }
        finally
        {
            await server.StopAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Server_UiAffinityCommand_ThroughCliClient_MarshalsToStubDispatcher()
    {
        StubUiDispatcher ui = new();
        PipeRpcServer server = CreateServer(ui);
        try
        {
            PipeRpcClient client = new(_pipeName, timeoutMilliseconds: 5_000);

            JsonRpcResponse response = await client.SendAsync(
                "widgets/list",
                JsonDocument.Parse("{}").RootElement.Clone(),
                clientVersion: "1.4.5").ConfigureAwait(true);

            Assert.Null(response.Error);
            Assert.Equal(1, ui.PostCount);
            Assert.Equal(0, response.Result!.Data!.Value.GetProperty("count").GetInt32());
        }
        finally
        {
            await server.StopAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task Server_TwoSequentialClients_BothServed()
    {
        PipeRpcServer server = CreateServer();
        try
        {
            PipeRpcClient first = new(_pipeName, timeoutMilliseconds: 5_000);
            PipeRpcClient second = new(_pipeName, timeoutMilliseconds: 10_000);

            JsonRpcResponse firstResponse = await first.SendAsync(
                "server/ping", JsonDocument.Parse("{}").RootElement.Clone(), clientVersion: "1.4.5").ConfigureAwait(true);
            JsonRpcResponse secondResponse = await second.SendAsync(
                "server/ping", JsonDocument.Parse("{}").RootElement.Clone(), clientVersion: "1.4.5").ConfigureAwait(true);

            Assert.Null(firstResponse.Error);
            Assert.Null(secondResponse.Error);
        }
        finally
        {
            await server.StopAsync().ConfigureAwait(true);
        }
    }

    private sealed class StubUiDispatcher : ICommandUiDispatcher
    {
        public int PostCount { get; private set; }

        public bool TryPost(Action work)
        {
            PostCount++;
            work();
            return true;
        }
    }
}
