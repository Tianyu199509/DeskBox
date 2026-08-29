namespace DeskBox.Protocol;

/// <summary>One argument accepted by a command, described for schema self-discovery.</summary>
public sealed record CommandArgumentDescriptor(
    string Name,
    string Type,
    bool Required,
    string? Description,
    string? ExampleJson);

/// <summary>
/// Static, serializable description of one command API method. This is the
/// contract surface consumed by <c>deskbox schema</c> and MCP tool
/// generation; changes to the shape of a command must be reflected here.
/// </summary>
public sealed record CommandDescriptor(
    string Method,
    string Category,
    string Capability,
    bool MutatesState,
    bool Destructive,
    string ThreadAffinity,
    string Summary,
    IReadOnlyList<CommandArgumentDescriptor> Arguments,
    string? ExampleRequestJson,
    string? ExampleResponseJson);

/// <summary>Full schema snapshot returned by <c>deskbox schema</c> and used by AI clients for self-discovery.</summary>
public sealed record CommandApiSchema(
    int ProtocolVersion,
    string ServerVersion,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<CommandDescriptor> Commands);
