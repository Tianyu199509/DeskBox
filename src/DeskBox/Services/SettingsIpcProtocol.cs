namespace DeskBox.Services;

using System.Text.Json.Serialization;

/// <summary>
/// Wire envelope for the settings-host IPC pipe (batch 3b of the
/// settings-process split): newline-delimited UTF-8 JSON over a duplex named
/// pipe. Payloads ride as JSON-or-plain strings so every command shape stays
/// source-generated at the envelope boundary and new methods need no wire
/// schema change.
/// </summary>
public sealed record SettingsIpcEnvelope(
    long Id,
    string Type,
    string Method,
    bool Ok,
    string? Payload,
    string? Error)
{
    public const string TypeRequest = "req";
    public const string TypeResponse = "res";
    public const string TypeEvent = "evt";
}

/// <summary>Method names on the settings-host pipe.</summary>
public static class SettingsIpcMethods
{
    /// <summary>Liveness probe; payload is ignored, response ok.</summary>
    public const string Ping = "ping";

    /// <summary>
    /// Settings host → main: apply a committed settings snapshot. Payload is
    /// "&lt;commitId&gt;\n&lt;settingsJson&gt;" (the commit id rides first so the
    /// response and the reload push can echo-suppress).
    /// </summary>
    public const string SettingsCommit = "settings-commit";

    /// <summary>
    /// Main → settings host: the durable settings changed on the main side;
    /// payload is the main commit id to echo-suppress.
    /// </summary>
    public const string SettingsReloaded = "settings-reloaded";
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SettingsIpcEnvelope))]
internal sealed partial class SettingsIpcJsonContext : JsonSerializerContext;
