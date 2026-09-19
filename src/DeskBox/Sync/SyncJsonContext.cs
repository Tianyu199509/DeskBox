using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Sync;

/// <summary>
/// Wire profile for every sync-protocol file: snake_case property names per
/// the envelope contract (§3.1). <see cref="JsonObject"/> payloads pass
/// through verbatim — their inner shape belongs to the domain stores'
/// camelCase profile, not this one.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(
    typeof(SyncEnvelope),
    TypeInfoPropertyName = "SyncEnvelope")]
internal sealed partial class SyncJsonContext : JsonSerializerContext
{
}
