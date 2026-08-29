using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Protocol;

/// <summary>
/// Standard JSON-RPC 2.0 error codes used by the command API. DeskBox-specific
/// semantics are carried in <see cref="CommandErrorPayload.Code"/>, not here.
/// </summary>
public static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>Implementation-defined server error code for DeskBox command failures.</summary>
    public const int ServerError = -32000;
}

/// <summary>JSON-RPC 2.0 request. Notifications (null id) are rejected by the command API.</summary>
public sealed class JsonRpcRequest
{
    /// <summary>Wire name is fixed by the JSON-RPC 2.0 spec: exactly "jsonrpc".</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    public JsonElement? Id { get; set; }

    public string Method { get; set; } = string.Empty;

    public CommandRequest? Params { get; set; }
}

/// <summary>JSON-RPC 2.0 error object with a typed DeskBox payload in <see cref="Data"/>.</summary>
public sealed class JsonRpcErrorObject
{
    public int Code { get; set; }

    public string Message { get; set; } = string.Empty;

    public CommandErrorPayload? Data { get; set; }
}

/// <summary>JSON-RPC 2.0 response: exactly one of <see cref="Result"/> or <see cref="Error"/> is set.</summary>
public sealed class JsonRpcResponse
{
    /// <summary>Wire name is fixed by the JSON-RPC 2.0 spec: exactly "jsonrpc".</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    public JsonElement? Id { get; set; }

    public CommandResult? Result { get; set; }

    public JsonRpcErrorObject? Error { get; set; }
}
