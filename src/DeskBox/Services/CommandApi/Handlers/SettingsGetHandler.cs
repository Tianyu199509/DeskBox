using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

public sealed record SettingsSnapshot(
    string Theme,
    string Language,
    string PerformanceMode,
    string PerformanceCacheBudget,
    bool AutoStart,
    bool AutoCheckForUpdates,
    bool QuickCaptureEnabled,
    bool TodoEnabled,
    bool EnableCommandApi,
    bool CommandApiReadOnly,
    bool AllowDestructiveCommands);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SettingsSnapshot), TypeInfoPropertyName = "SettingsSnapshot")]
internal sealed partial class SettingsGetJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Returns an explicit allowlist of settings. Deliberately a projection,
/// never a dump: the settings model may grow keys that are not safe to
/// expose to every local process, so new fields must be added consciously.
/// </summary>
public sealed class SettingsGetHandler : ICommandHandler
{
    private readonly Func<AppSettings> _settings;

    public SettingsGetHandler(Func<AppSettings> settings)
    {
        _settings = settings;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "settings/get",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.SettingsRead,
        MutatesState: false,
        Destructive: false,
        Summary: "Returns an allowlisted snapshot of application settings (no secrets, no paths).",
        Arguments: [],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":7,"method":"settings/get","params":{"protocolVersion":1,"clientName":"deskbox-cli"}}""",
        ExampleResponseJson: """{"result":{"data":{"theme":"System","language":"System","commandApiReadOnly":false}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        AppSettings settings = _settings();
        SettingsSnapshot snapshot = new(
            settings.Theme,
            settings.Language,
            settings.PerformanceMode,
            settings.PerformanceCacheBudget,
            settings.AutoStart,
            settings.AutoCheckForUpdates,
            settings.QuickCaptureEnabled,
            settings.TodoEnabled,
            settings.EnableCommandApi,
            settings.CommandApiReadOnly,
            settings.AllowDestructiveCommands);
        return Task.FromResult(JsonSerializer.SerializeToElement(snapshot, SettingsGetJsonContext.Default.SettingsSnapshot));
    }
}
