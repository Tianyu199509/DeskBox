using System.Text.Json;

namespace DeskBox.Protocol;

/// <summary>
/// Typed params payload carried as the JSON-RPC "params" member of every
/// command API request. Per-command arguments travel inside
/// <see cref="Arguments"/> as raw JSON so the envelope can evolve without
/// touching the wire contract.
/// </summary>
public sealed class CommandRequest
{
    public int ProtocolVersion { get; set; } = CommandApiProtocol.ProtocolVersion;

    public string ClientName { get; set; } = string.Empty;

    public string ClientVersion { get; set; } = string.Empty;

    public int ClientProcessId { get; set; }

    /// <summary>
    /// Optional key making a mutating command idempotent: a retry with the
    /// same key must not repeat the state change.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Validate the request and report the outcome without mutating state.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Per-command arguments. Null (and therefore omitted on the wire) when
    /// the command takes no arguments; never serialize a default
    /// <see cref="JsonElement"/>, whose <c>Undefined</c> value throws on write.
    /// </summary>
    public JsonElement? Arguments { get; set; }
}

/// <summary>
/// Structured error detail transported inside the JSON-RPC error "data"
/// member. <see cref="Code"/> values come from
/// <see cref="CommandApiProtocol.ErrorCodes"/> and are stable contract
/// surface; <see cref="Hint"/> is written for machine consumption so AI
/// clients can self-correct and retry.
/// </summary>
public sealed class CommandErrorPayload
{
    public string Code { get; set; } = string.Empty;

    /// <summary>Pipeline phase that rejected the request: auth, route, validate, or execute.</summary>
    public string Phase { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Hint { get; set; }
}

/// <summary>Typed result payload carried as the JSON-RPC "result" member.</summary>
public sealed class CommandResult
{
    public int ProtocolVersion { get; set; }

    public string ServerVersion { get; set; } = string.Empty;

    /// <summary>Full capability list advertised by the running server.</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>
    /// Per-command result data. Null (and therefore omitted on the wire)
    /// when the command returns no data.
    /// </summary>
    public JsonElement? Data { get; set; }
}
