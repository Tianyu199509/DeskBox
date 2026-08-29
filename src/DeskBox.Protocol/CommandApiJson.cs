using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Protocol;

/// <summary>
/// Reflection-free JSON contract for the command API. Source generation is
/// mandatory: the server and the CLI both run under NativeAOT where
/// reflective serialization is unavailable.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(CommandErrorPayload))]
[JsonSerializable(typeof(CommandApiSchema))]
[JsonSerializable(typeof(CommandDescriptor))]
[JsonSerializable(typeof(CommandArgumentDescriptor))]
internal sealed partial class CommandApiJsonContext : JsonSerializerContext
{
}

/// <summary>Serialization entry points so callers never touch the internal context directly.</summary>
public static class CommandApiJson
{
    public static string SerializeRequest(JsonRpcRequest request)
        => JsonSerializer.Serialize(request, CommandApiJsonContext.Default.JsonRpcRequest);

    public static string SerializeResponse(JsonRpcResponse response)
        => JsonSerializer.Serialize(response, CommandApiJsonContext.Default.JsonRpcResponse);

    public static string SerializeSchema(CommandApiSchema schema)
        => JsonSerializer.Serialize(schema, CommandApiJsonContext.Default.CommandApiSchema);

    public static JsonRpcRequest? DeserializeRequest(string json)
        => JsonSerializer.Deserialize(json, CommandApiJsonContext.Default.JsonRpcRequest);

    public static JsonRpcRequest? DeserializeRequest(ReadOnlySpan<byte> utf8Json)
        => JsonSerializer.Deserialize(utf8Json, CommandApiJsonContext.Default.JsonRpcRequest);

    public static JsonRpcResponse? DeserializeResponse(string json)
        => JsonSerializer.Deserialize(json, CommandApiJsonContext.Default.JsonRpcResponse);

    public static JsonRpcResponse? DeserializeResponse(ReadOnlySpan<byte> utf8Json)
        => JsonSerializer.Deserialize(utf8Json, CommandApiJsonContext.Default.JsonRpcResponse);

    public static CommandApiSchema? DeserializeSchema(string json)
        => JsonSerializer.Deserialize(json, CommandApiJsonContext.Default.CommandApiSchema);
}
