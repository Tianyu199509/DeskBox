using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

/// <summary>
/// Serves the full machine-readable schema (commands, arguments, examples)
/// from the registry. This is the self-discovery entry point AI clients use
/// instead of documentation: the registry is the single source of truth, so
/// the schema can never drift from what the server actually implements.
/// </summary>
public sealed class ServerSchemaHandler : ICommandHandler
{
    private readonly Func<CommandRegistry> _registry;
    private readonly string _serverVersion;

    public ServerSchemaHandler(Func<CommandRegistry> registry, string serverVersion)
    {
        _registry = registry;
        _serverVersion = serverVersion;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "server/schema",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.ServerInfo,
        MutatesState: false,
        Destructive: false,
        Summary: "Returns the complete command API schema: every method, its arguments, capabilities, and examples.",
        Arguments: [],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":9,"method":"server/schema","params":{"protocolVersion":1,"clientName":"deskbox-cli"}}""",
        ExampleResponseJson: """{"result":{"data":{"protocolVersion":1,"serverVersion":"1.4.5","capabilities":["server.info"],"commands":[]}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        CommandApiSchema schema = _registry().BuildSchema(_serverVersion);
        return Task.FromResult(JsonSerializer.SerializeToElement(
            schema,
            CommandApiSchemaJsonContext.Default.SchemaSnapshot));
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandApiSchema), TypeInfoPropertyName = "SchemaSnapshot")]
[JsonSerializable(typeof(CommandDescriptor), TypeInfoPropertyName = "SchemaCommand")]
[JsonSerializable(typeof(CommandArgumentDescriptor), TypeInfoPropertyName = "SchemaArgument")]
internal sealed partial class CommandApiSchemaJsonContext : JsonSerializerContext
{
}
