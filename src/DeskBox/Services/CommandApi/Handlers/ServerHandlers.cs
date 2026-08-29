using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

public sealed record ServerPingResult(bool Pong, string TimestampUtc, int ProtocolVersion);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServerPingResult), TypeInfoPropertyName = "PingResult")]
internal sealed partial class ServerPingJsonContext : JsonSerializerContext
{
}

/// <summary>Liveness probe. Cheap by design: AI clients poll this to detect a running app.</summary>
public sealed class ServerPingHandler : ICommandHandler
{
    public CommandRegistration Registration { get; } = new(
        Method: "server/ping",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.ServerInfo,
        MutatesState: false,
        Destructive: false,
        Summary: "Liveness probe; returns pong with the server protocol version.",
        Arguments: [],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":1,"method":"server/ping","params":{"protocolVersion":1,"clientName":"deskbox-cli","clientVersion":"1.4.5"}}""",
        ExampleResponseJson: """{"result":{"protocolVersion":1,"data":{"pong":true,"timestampUtc":"2026-08-29T10:00:00.000Z","protocolVersion":1}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        ServerPingResult result = new(
            Pong: true,
            TimestampUtc: DateTimeOffset.UtcNow.ToString("O"),
            ProtocolVersion: CommandApiProtocol.ProtocolVersion);
        return Task.FromResult(JsonSerializer.SerializeToElement(result, ServerPingJsonContext.Default.PingResult));
    }
}

public sealed record ServerInfoResult(
    int ProtocolVersion,
    string ServerVersion,
    int CommandCount,
    IReadOnlyList<string> Capabilities,
    int UptimeSeconds,
    bool ReadOnlyMode,
    bool DestructiveAllowed);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServerInfoResult), TypeInfoPropertyName = "InfoResult")]
internal sealed partial class ServerInfoJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Capability negotiation entry point: clients call this first, read the
/// capability list, and must not call commands whose capability is absent.
/// </summary>
public sealed class ServerInfoHandler : ICommandHandler
{
    private readonly Func<CommandRegistry> _registry;
    private readonly string _serverVersion;
    private readonly Func<DateTimeOffset> _processStartUtc;
    private readonly Func<bool> _isReadOnlyMode;
    private readonly Func<bool> _allowsDestructive;

    public ServerInfoHandler(
        Func<CommandRegistry> registry,
        string serverVersion,
        Func<bool> isReadOnlyMode,
        Func<bool> allowsDestructive,
        Func<DateTimeOffset>? processStartUtc = null)
    {
        _registry = registry;
        _serverVersion = serverVersion;
        if (processStartUtc is null)
        {
            _processStartUtc = () => Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        else
        {
            _processStartUtc = processStartUtc;
        }
        _isReadOnlyMode = isReadOnlyMode;
        _allowsDestructive = allowsDestructive;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "server/info",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.ServerInfo,
        MutatesState: false,
        Destructive: false,
        Summary: "Returns protocol version, server version, uptime, capability list, and current command API policy.",
        Arguments: [],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":2,"method":"server/info","params":{"protocolVersion":1,"clientName":"deskbox-cli"}}""",
        ExampleResponseJson: """{"result":{"protocolVersion":1,"serverVersion":"1.4.5","capabilities":["server.info","todo.read"],"data":{"protocolVersion":1,"commandCount":8,"readOnlyMode":false}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        ServerInfoResult result = new(
            ProtocolVersion: CommandApiProtocol.ProtocolVersion,
            ServerVersion: _serverVersion,
            CommandCount: _registry().Count,
            Capabilities: _registry().GetCapabilities(),
            UptimeSeconds: (int)Math.Max(0, (DateTimeOffset.UtcNow - _processStartUtc()).TotalSeconds),
            ReadOnlyMode: _isReadOnlyMode(),
            DestructiveAllowed: _allowsDestructive());
        return Task.FromResult(JsonSerializer.SerializeToElement(result, ServerInfoJsonContext.Default.InfoResult));
    }
}
