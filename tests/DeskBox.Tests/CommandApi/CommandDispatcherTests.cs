using System.Text.Json;
using DeskBox.Protocol;
using DeskBox.Services.CommandApi;

namespace DeskBox.Tests.CommandApi;

public class CommandDispatcherTests
{
    private static CommandRequest DefaultParams(string method, JsonElement? arguments = null)
        => new()
        {
            ProtocolVersion = CommandApiProtocol.ProtocolVersion,
            ClientName = "test-client",
            Arguments = arguments ?? JsonSerializer.SerializeToElement(new { }),
        };

    private static JsonRpcRequest Request(string method, CommandRequest? command = null)
        => new()
        {
            Id = JsonSerializer.SerializeToElement(1),
            Method = method,
            Params = command ?? DefaultParams(method),
        };

    private static CommandDispatcher CreateDispatcher(
        IEnumerable<ICommandHandler> handlers,
        StubUiDispatcher? uiDispatcher = null,
        bool readOnly = false,
        bool destructive = false,
        TimeSpan? uiTimeout = null)
        => new(
            new CommandRegistry(handlers),
            () => readOnly,
            () => destructive,
            uiDispatcher ?? new StubUiDispatcher(),
            serverVersion: "1.4.5",
            uiDispatchTimeout: uiTimeout);

    [Fact]
    public async Task DispatchAsync_UnknownMethod_ReturnsMethodNotFoundWithSchemaHint()
    {
        CommandDispatcher dispatcher = CreateDispatcher([StubHandler.ReadOnly("server/ping")]);

        JsonRpcResponse response = await dispatcher.DispatchAsync(Request("nope/missing"), CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, response.Error!.Code);
        Assert.Equal(CommandApiProtocol.ErrorCodes.MethodNotFound, response.Error.Data!.Code);
        Assert.Contains("deskbox schema", response.Error.Data.Hint);
    }

    [Fact]
    public async Task DispatchAsync_VersionMismatch_ReturnsProtocolVersionMismatch()
    {
        CommandDispatcher dispatcher = CreateDispatcher([StubHandler.ReadOnly("server/ping")]);
        CommandRequest command = DefaultParams("server/ping");
        command.ProtocolVersion = 99;

        JsonRpcResponse response = await dispatcher.DispatchAsync(Request("server/ping", command), CancellationToken.None);

        Assert.Equal(
            CommandApiProtocol.ErrorCodes.ProtocolVersionMismatch,
            response.Error!.Data!.Code);
    }

    [Fact]
    public async Task DispatchAsync_EmptyMethod_ReturnsInvalidRequest()
    {
        CommandDispatcher dispatcher = CreateDispatcher([StubHandler.ReadOnly("server/ping")]);

        JsonRpcResponse response = await dispatcher.DispatchAsync(
            new JsonRpcRequest { Id = JsonSerializer.SerializeToElement(1), Method = string.Empty, Params = DefaultParams("") },
            CancellationToken.None);

        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, response.Error!.Code);
    }

    [Fact]
    public async Task DispatchAsync_MissingParams_ReturnsInvalidRequest()
    {
        CommandDispatcher dispatcher = CreateDispatcher([StubHandler.ReadOnly("server/ping")]);

        JsonRpcResponse response = await dispatcher.DispatchAsync(
            new JsonRpcRequest { Id = JsonSerializer.SerializeToElement(1), Method = "server/ping" },
            CancellationToken.None);

        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, response.Error!.Code);
        Assert.Equal("route", response.Error.Data!.Phase);
    }

    [Fact]
    public async Task DispatchAsync_MutatingCommand_InReadOnlyMode_IsRejectedBeforeHandlerRuns()
    {
        StubHandler handler = StubHandler.Mutating("quickcapture/add");
        CommandDispatcher dispatcher = CreateDispatcher([handler], readOnly: true);

        JsonRpcResponse response = await dispatcher.DispatchAsync(Request("quickcapture/add"), CancellationToken.None);

        Assert.Equal(CommandApiProtocol.ErrorCodes.ReadOnlyMode, response.Error!.Data!.Code);
        Assert.Equal("auth", response.Error.Data.Phase);
        Assert.Equal(0, handler.ExecutionCount);
    }

    [Fact]
    public async Task DispatchAsync_MutatingCommand_AllowedWhenNotReadOnly_ReturnsHandlerData()
    {
        StubHandler handler = StubHandler.Mutating("quickcapture/add");
        CommandDispatcher dispatcher = CreateDispatcher([handler]);

        JsonRpcResponse response = await dispatcher.DispatchAsync(Request("quickcapture/add"), CancellationToken.None);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        Assert.Equal(1, handler.ExecutionCount);
        Assert.Equal("stub-data", response.Result!.Data!.Value.GetProperty("value").GetString());
        Assert.Equal(CommandApiProtocol.ProtocolVersion, response.Result.ProtocolVersion);
        Assert.Equal("1.4.5", response.Result.ServerVersion);
    }

    [Fact]
    public async Task DispatchAsync_DestructiveCommand_IsGatedUntilEnabled()
    {
        StubHandler handler = StubHandler.Destructive("organize/apply");
        CommandDispatcher gatedDispatcher = CreateDispatcher([handler], destructive: false);

        JsonRpcResponse gated = await gatedDispatcher.DispatchAsync(Request("organize/apply"), CancellationToken.None);

        Assert.Equal(CommandApiProtocol.ErrorCodes.DestructiveDisabled, gated.Error!.Data!.Code);

        CommandDispatcher openDispatcher = CreateDispatcher([handler], destructive: true);
        JsonRpcResponse allowed = await openDispatcher.DispatchAsync(Request("organize/apply"), CancellationToken.None);

        Assert.Null(allowed.Error);
        Assert.Equal(1, handler.ExecutionCount);
    }

    [Fact]
    public async Task DispatchAsync_HandlerValidationFailure_MapsToInvalidParamsWithHint()
    {
        CommandDispatcher dispatcher = CreateDispatcher([StubHandler.Validating("todo/list")]);

        JsonRpcResponse response = await dispatcher.DispatchAsync(Request("todo/list"), CancellationToken.None);

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, response.Error!.Code);
        Assert.Equal(CommandApiProtocol.ErrorCodes.ValidationFailed, response.Error.Data!.Code);
        Assert.Equal("validate", response.Error.Data.Phase);
        Assert.False(string.IsNullOrWhiteSpace(response.Error.Data.Hint));
    }

    [Fact]
    public async Task DispatchAsync_UiAffinityCommand_ExecutesThroughDispatcher()
    {
        StubUiDispatcher ui = new();
        CommandDispatcher dispatcher = CreateDispatcher(
            [StubHandler.OnUi("widgets/list")],
            uiDispatcher: ui);

        JsonRpcResponse response = await dispatcher.DispatchAsync(Request("widgets/list"), CancellationToken.None);

        Assert.Null(response.Error);
        Assert.Equal(1, ui.PostCount);
        Assert.Equal("stub-data", response.Result!.Data!.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task DispatchAsync_UiAffinityCommand_WhenQueueRejected_ReturnsInternalError()
    {
        StubUiDispatcher ui = new() { AcceptsWork = false };
        CommandDispatcher dispatcher = CreateDispatcher(
            [StubHandler.OnUi("widgets/list")],
            uiDispatcher: ui);

        JsonRpcResponse response = await dispatcher.DispatchAsync(Request("widgets/list"), CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(1, ui.PostCount);
    }

    [Fact]
    public async Task DispatchAsync_UiAffinityCommand_WhenUiThreadStalled_ReturnsUiBusy()
    {
        StubUiDispatcher ui = new() { InvokeWork = false };
        CommandDispatcher dispatcher = CreateDispatcher(
            [StubHandler.OnUi("widgets/list")],
            uiDispatcher: ui,
            uiTimeout: TimeSpan.FromMilliseconds(80));

        JsonRpcResponse response = await dispatcher.DispatchAsync(Request("widgets/list"), CancellationToken.None);

        Assert.Equal(CommandApiProtocol.ErrorCodes.UiBusy, response.Error!.Data!.Code);
        Assert.Contains("idempotencyKey", response.Error.Data.Hint);
    }

    private sealed class StubUiDispatcher : ICommandUiDispatcher
    {
        public bool AcceptsWork { get; init; } = true;

        /// <summary>When false, posted work is parked forever (simulates a stalled UI thread).</summary>
        public bool InvokeWork { get; init; } = true;

        public int PostCount { get; private set; }

        public bool TryPost(Action work)
        {
            PostCount++;
            if (!AcceptsWork)
            {
                return false;
            }

            if (InvokeWork)
            {
                work();
            }

            return true;
        }
    }

    private sealed class StubHandler : ICommandHandler
    {
        private readonly bool _alwaysFailsValidation;

        private StubHandler(CommandRegistration registration, bool alwaysFailsValidation)
        {
            Registration = registration;
            _alwaysFailsValidation = alwaysFailsValidation;
        }

        public CommandRegistration Registration { get; }

        public int ExecutionCount { get; private set; }

        public Task<JsonElement> ExecuteAsync(
            JsonElement arguments,
            CommandExecutionContext context,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            if (_alwaysFailsValidation)
            {
                throw new CommandValidationException(CommandArguments.ValidationFailed(
                    "validation failed",
                    "fix the arguments"));
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { value = "stub-data" }));
        }

        public static StubHandler ReadOnly(string method)
            => new(CreateRegistration(method, mutates: false, destructive: false, ui: false), alwaysFailsValidation: false);

        public static StubHandler Mutating(string method)
            => new(CreateRegistration(method, mutates: true, destructive: false, ui: false), alwaysFailsValidation: false);

        public static StubHandler Destructive(string method)
            => new(CreateRegistration(method, mutates: true, destructive: true, ui: false), alwaysFailsValidation: false);

        public static StubHandler OnUi(string method)
            => new(CreateRegistration(method, mutates: false, destructive: false, ui: true), alwaysFailsValidation: false);

        public static StubHandler Validating(string method)
            => new(CreateRegistration(method, mutates: false, destructive: false, ui: false), alwaysFailsValidation: true);

        private static CommandRegistration CreateRegistration(
            string method, bool mutates, bool destructive, bool ui)
            => new(
                Method: method,
                ThreadAffinity: ui ? CommandThreadAffinity.UiThread : CommandThreadAffinity.Any,
                Capability: CommandApiProtocol.Capabilities.ServerInfo,
                MutatesState: mutates,
                Destructive: destructive,
                Summary: "stub",
                Arguments: [],
                ExampleRequestJson: null,
                ExampleResponseJson: null);
    }
}
