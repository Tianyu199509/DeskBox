using System.Buffers.Binary;
using System.Text;
using DeskBox.Protocol;

namespace DeskBox.Tests.CommandApi;

public class CommandApiProtocolTests
{
    [Fact]
    public void CommandFrame_WriteThenRead_RoundTripsPayload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1}""");
        using MemoryStream stream = new();

        CommandFrame.WriteAsync(stream, payload).GetAwaiter().GetResult();
        stream.Position = 0;
        byte[] roundTripped = CommandFrame.ReadAsync(stream).GetAwaiter().GetResult();

        Assert.Equal(payload, roundTripped);
    }

    [Fact]
    public void CommandFrame_ReadAsync_RejectsOversizedLengthPrefix()
    {
        using MemoryStream stream = new();
        byte[] prefix = new byte[CommandFrame.LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, CommandApiProtocol.MaxFrameBytes + 1u);
        stream.Write(prefix);
        stream.Position = 0;

        Assert.ThrowsAny<InvalidDataException>(
            () => CommandFrame.ReadAsync(stream).GetAwaiter().GetResult());
    }

    [Fact]
    public void CommandApiJson_SerializesRequestWithCamelCaseProperties()
    {
        JsonRpcRequest request = new()
        {
            Id = System.Text.Json.JsonSerializer.SerializeToElement(7),
            Method = "server/ping",
            Params = new CommandRequest
            {
                ClientName = "test-client",
                ClientProcessId = 42,
            },
        };

        string json = CommandApiJson.SerializeRequest(request);

        Assert.Contains("\"jsonrpc\":\"2.0\"", json);
        Assert.Contains("\"method\":\"server/ping\"", json);
        Assert.Contains("\"clientName\":\"test-client\"", json);
        Assert.Contains("\"clientProcessId\":42", json);
    }

    [Fact]
    public void CommandApiJson_RoundTripsResponseWithErrorPayload()
    {
        JsonRpcResponse response = new()
        {
            Id = System.Text.Json.JsonSerializer.SerializeToElement(9),
            Error = new JsonRpcErrorObject
            {
                Code = JsonRpcErrorCodes.ServerError,
                Message = "rejected",
                Data = new CommandErrorPayload
                {
                    Code = CommandApiProtocol.ErrorCodes.ReadOnlyMode,
                    Phase = "auth",
                    Message = "rejected",
                    Hint = "turn off read-only",
                },
            },
        };

        JsonRpcResponse? roundTripped = CommandApiJson.DeserializeResponse(
            CommandApiJson.SerializeResponse(response));

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped!.Result);
        Assert.NotNull(roundTripped.Error);
        Assert.Equal(CommandApiProtocol.ErrorCodes.ReadOnlyMode, roundTripped.Error!.Data!.Code);
        Assert.Equal("auth", roundTripped.Error.Data.Phase);
        Assert.Equal("turn off read-only", roundTripped.Error.Data.Hint);
    }

    [Fact]
    public void CommandApiJson_SchemaRoundTripsWithDescriptors()
    {
        CommandApiSchema schema = new(
            ProtocolVersion: CommandApiProtocol.ProtocolVersion,
            ServerVersion: "1.4.5",
            Capabilities: [CommandApiProtocol.Capabilities.ServerInfo],
            Commands:
            [
                new CommandDescriptor(
                    Method: "server/ping",
                    Category: "server",
                    Capability: CommandApiProtocol.Capabilities.ServerInfo,
                    MutatesState: false,
                    Destructive: false,
                    ThreadAffinity: "any",
                    Summary: "probe",
                    Arguments:
                    [
                        new CommandArgumentDescriptor("limit", "integer", false, "cap", "50"),
                    ],
                    ExampleRequestJson: "{}",
                    ExampleResponseJson: "{}"),
            ]);

        CommandApiSchema? roundTripped = CommandApiJson.DeserializeSchema(
            CommandApiJson.SerializeSchema(schema));

        Assert.NotNull(roundTripped);
        Assert.Single(roundTripped!.Commands);
        Assert.Equal("server/ping", roundTripped.Commands[0].Method);
        Assert.Single(roundTripped.Commands[0].Arguments);
        Assert.Equal("limit", roundTripped.Commands[0].Arguments[0].Name);
    }
}
