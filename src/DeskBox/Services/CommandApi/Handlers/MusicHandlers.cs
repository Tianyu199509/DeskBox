using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;
using DeskBox.ViewModels;

namespace DeskBox.Services.CommandApi.Handlers;

/// <summary>
/// Music widget commands. DeskBox has no built-in player: playback control
/// goes through SMTC (System Media Transport Controls) to whichever player
/// is currently playing (QQ Music, Spotify, a browser, …). All handlers
/// run on the UI thread through the live view model, whose bound
/// properties the open widget renders.
/// </summary>
internal static class MusicAccess
{
    public static MusicWidgetViewModel RequireViewModel(
        Func<string, MusicWidgetViewModel?> resolver,
        string widgetId)
    {
        MusicWidgetViewModel? viewModel = resolver(widgetId);
        if (viewModel is null)
        {
            throw new CommandValidationException(new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.WidgetNotLoaded,
                Phase = "execute",
                Message = $"Music widget '{widgetId}' is configured but not currently loaded.",
                Hint = "Call widgets/show with this widgetId first, then retry.",
            });
        }

        return viewModel;
    }
}

public sealed record MusicStatusResult(
    string WidgetId,
    string Title,
    string Artist,
    string PlaybackState,
    bool IsPlaying,
    int SystemVolumePercent);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MusicStatusResult), TypeInfoPropertyName = "MusicStatusResult")]
internal sealed partial class MusicJsonContext : JsonSerializerContext
{
}

/// <summary>Reads the music widget's live SMTC snapshot: title, artist,
/// playback state, and system volume.</summary>
public sealed class MusicStatusHandler : ICommandHandler
{
    private readonly Func<string, MusicWidgetViewModel?> _resolver;

    public MusicStatusHandler(Func<string, MusicWidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "music/status",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.MusicRead,
        MutatesState: false,
        Destructive: false,
        Summary: "Reads the music widget's current SMTC snapshot (title, artist, playback state, system volume).",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Music widget id (from widgets/list).", "\"m1\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":32,"method":"music/status","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"m1"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"m1","title":"Song","artist":"Artist","playbackState":"Playing","isPlaying":true,"systemVolumePercent":40}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = CommandArguments.RequireWidgetId(arguments);
        MusicWidgetViewModel viewModel = MusicAccess.RequireViewModel(_resolver, widgetId);
        MusicStatusResult result = new(
            widgetId,
            viewModel.Title,
            viewModel.Artist,
            viewModel.PlaybackState.ToString(),
            viewModel.IsPlaying,
            (int)Math.Round(Math.Clamp(viewModel.SystemVolume, 0, 1) * 100));
        return Task.FromResult(JsonSerializer.SerializeToElement(result, MusicJsonContext.Default.MusicStatusResult));
    }
}

public sealed record MusicCommandResult(string WidgetId, string Action, bool Ok);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MusicCommandResult), TypeInfoPropertyName = "MusicCommandResult")]
internal sealed partial class MusicCommandJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Shared transport-style playback control. The underlying SMTC Try*
/// calls can be silently rejected by the target player; the returned ok
/// reflects what the player acknowledged.
/// </summary>
public sealed class MusicTransportHandler : ICommandHandler
{
    private readonly Func<string, MusicWidgetViewModel?> _resolver;
    private readonly string _action;

    private MusicTransportHandler(
        Func<string, MusicWidgetViewModel?> resolver,
        string action,
        string method,
        string capability,
        string summary)
    {
        _resolver = resolver;
        _action = action;
        Registration = new CommandRegistration(
            Method: method,
            ThreadAffinity: CommandThreadAffinity.UiThread,
            Capability: capability,
            MutatesState: true,
            Destructive: false,
            Summary: summary,
            Arguments:
            [
                new CommandArgumentDescriptor("widgetId", "string", true, "Music widget id.", "\"m1\""),
            ],
            ExampleRequestJson: $"{{\"jsonrpc\":\"2.0\",\"id\":33,\"method\":\"{method}\",\"params\":{{\"protocolVersion\":1,\"clientName\":\"deskbox-cli\",\"arguments\":{{\"widgetId\":\"m1\"}}}}}}",
            ExampleResponseJson: """{"result":{"data":{"widgetId":"m1","action":"play","ok":true}}}""");
    }

    public CommandRegistration Registration { get; }

    public static MusicTransportHandler Toggle(Func<string, MusicWidgetViewModel?> resolver)
        => new(resolver, "toggle", "music/toggle", CommandApiProtocol.Capabilities.MusicWrite,
            "Toggles play/pause on the current SMTC media session.");

    public static MusicTransportHandler Previous(Func<string, MusicWidgetViewModel?> resolver)
        => new(resolver, "previous", "music/previous", CommandApiProtocol.Capabilities.MusicWrite,
            "Sends a previous-track command to the current SMTC media session.");

    public static MusicTransportHandler Next(Func<string, MusicWidgetViewModel?> resolver)
        => new(resolver, "next", "music/next", CommandApiProtocol.Capabilities.MusicWrite,
            "Sends a next-track command to the current SMTC media session.");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = CommandArguments.RequireWidgetId(arguments);
        MusicWidgetViewModel viewModel = MusicAccess.RequireViewModel(_resolver, widgetId);

        // SMTC transport methods are void: a true result means the command
        // was dispatched; whether the player honors it is reflected in
        // music/status afterwards.
        bool ok = true;
        switch (_action)
        {
            case "toggle":
                await viewModel.TogglePlayPauseAsync().ConfigureAwait(true);
                break;
            case "previous":
                await viewModel.PreviousAsync().ConfigureAwait(true);
                break;
            case "next":
                await viewModel.NextAsync().ConfigureAwait(true);
                break;
            default:
                ok = false;
                break;
        }

        MusicCommandResult result = new(widgetId, _action, ok);
        return JsonSerializer.SerializeToElement(result, MusicCommandJsonContext.Default.MusicCommandResult);
    }
}

/// <summary>Sets the system master volume (0-100) through the widget's
/// Core Audio backend.</summary>
public sealed class MusicVolumeHandler : ICommandHandler
{
    private readonly Func<string, MusicWidgetViewModel?> _resolver;

    public MusicVolumeHandler(Func<string, MusicWidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "music/volume",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.MusicWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Sets the system master volume (0-100) via the widget's Core Audio backend.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Music widget id.", "\"m1\""),
            new CommandArgumentDescriptor("volume", "integer", true, "System volume percent 0-100.", "40"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":34,"method":"music/volume","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"m1","volume":40}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"m1","action":"volume","ok":true}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = CommandArguments.RequireWidgetId(arguments);
        MusicWidgetViewModel viewModel = MusicAccess.RequireViewModel(_resolver, widgetId);
        if (!CommandArguments.TryGetInt(arguments, "volume", out int volume)
            || volume < 0
            || volume > 100)
        {
            throw CommandValidationException.ValidationFailed(
                "The 'volume' argument must be an integer between 0 and 100.",
                """Retry with {"volume":40}.""");
        }

        await viewModel.SetSystemVolumeAsync(volume / 100.0).ConfigureAwait(true);
        MusicCommandResult result = new(widgetId, "volume", true);
        return JsonSerializer.SerializeToElement(result, MusicCommandJsonContext.Default.MusicCommandResult);
    }
}
