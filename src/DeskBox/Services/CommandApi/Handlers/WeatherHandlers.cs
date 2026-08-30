using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

/// <summary>
/// Weather commands. WeatherService is fully headless (static HttpClient,
/// cache under the data root) with MSN as primary source and Open-Meteo as
/// fallback; the widget UI refreshes from settings/cache on its own cycle.
/// </summary>
public sealed record WeatherSetCityResult(string City, double Latitude, double Longitude, bool Applied);

public sealed record WeatherGetResult(
    string? LocationName,
    double? Temperature,
    string? WeatherCode,
    bool Stale,
    bool Fallback);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WeatherGetResult), TypeInfoPropertyName = "WeatherGetResult")]
[JsonSerializable(typeof(WeatherSetCityResult), TypeInfoPropertyName = "WeatherSetCityResult")]
internal sealed partial class WeatherJsonContext : JsonSerializerContext
{
}

/// <summary>Fetches current weather for the configured location
/// (force-refreshes past the 30-minute cache).</summary>
public sealed class WeatherGetHandler : ICommandHandler
{
    private readonly Func<WeatherService?> _weatherService;

    public WeatherGetHandler(Func<WeatherService?> weatherService)
    {
        _weatherService = weatherService;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "weather/get",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.WeatherRead,
        MutatesState: false,
        Destructive: false,
        Summary: "Fetches current weather for the configured location (external HTTP; MSN with Open-Meteo fallback).",
        Arguments:
        [
            new CommandArgumentDescriptor("forceRefresh", "boolean", false,
                "Bypass the 30-minute cache (default false).", "true"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":35,"method":"weather/get","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"forceRefresh":true}}}""",
        ExampleResponseJson: """{"result":{"data":{"locationName":"北京","temperature":22.5,"stale":false,"fallback":false}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        WeatherService? service = _weatherService()
            ?? throw WidgetLifecycle.NotLoaded("weather-service", "DeskBox is still starting; retry shortly.");
        bool forceRefresh = CommandArguments.TryGetBool(arguments, "forceRefresh", out bool force) && force;

        AppSettings settings = App.Current.SettingsService.Settings;
        WeatherData? data = await service
            .GetWeatherAsync(
                settings.WeatherLatitude,
                settings.WeatherLongitude,
                settings.WeatherCityName,
                forceRefresh,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (data?.Current is null)
        {
            throw new CommandValidationException(new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.InternalError,
                Phase = "execute",
                Message = "Weather fetch returned no data.",
                Hint = "The MSN/Open-Meteo backends may be unreachable (6s HTTP timeout) or no location is configured; retry or set a city in DeskBox settings.",
            });
        }

        WeatherGetResult result = new(
            data.LocationName ?? settings.WeatherCityName,
            data.Current.Temperature,
            data.Current.WeatherCode.ToString(),
            data.IsStale,
            data.IsFallback);
        return JsonSerializer.SerializeToElement(result, WeatherJsonContext.Default.WeatherGetResult);
    }
}

/// <summary>Sets the weather location by city name: resolves coordinates
/// via geocoding, persists them, and the widget refreshes from the
/// settings-changed notification.</summary>
public sealed class WeatherSetCityHandler : ICommandHandler
{
    private readonly Func<WeatherService?> _weatherService;
    private readonly Func<CitySearchService?> _citySearchService;

    public WeatherSetCityHandler(
        Func<WeatherService?> weatherService,
        Func<CitySearchService?> citySearchService)
    {
        _weatherService = weatherService;
        _citySearchService = citySearchService;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "weather/set-city",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.SettingsWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Sets the weather location by city name (geocoded, persisted; all weather widgets refresh automatically).",
        Arguments:
        [
            new CommandArgumentDescriptor("city", "string", true, "City name to geocode.", "\"上海\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":36,"method":"weather/set-city","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"city":"上海"}}}""",
        ExampleResponseJson: """{"result":{"data":{"city":"上海","latitude":31.23,"longitude":121.47,"applied":true}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        WeatherService? weatherService = _weatherService()
            ?? throw WidgetLifecycle.NotLoaded("weather-service", "DeskBox is still starting; retry shortly.");
        CitySearchService? citySearch = _citySearchService()
            ?? throw WidgetLifecycle.NotLoaded("city-search-service", "DeskBox is still starting; retry shortly.");
        if (!CommandArguments.TryGetString(arguments, "city", out string city)
            || string.IsNullOrWhiteSpace(city))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'city' argument is required.",
                """Retry with {"city":"上海"}.""");
        }

        List<WeatherCitySearchResult> matches = await citySearch
            .SearchAsync(city, cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        WeatherCitySearchResult? match = matches.FirstOrDefault()
            ?? throw CommandValidationException.ValidationFailed(
                $"City '{city}' could not be geocoded.",
                "Try a better-known city name (local or English spelling).");

        AppSettings settings = App.Current.SettingsService.Settings;
        if (!WeatherSettingsPolicy.TrySetManualLocation(
                settings, match.Name, match.Latitude, match.Longitude))
        {
            throw new CommandValidationException(new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.InternalError,
                Phase = "execute",
                Message = "The location policy rejected the new city.",
                Hint = "Check the DeskBox log for policy details.",
            });
        }

        await App.Current.SettingsService.SaveDebounced().ConfigureAwait(true);
        WeatherSetCityResult result = new(match.Name, match.Latitude, match.Longitude, true);
        return JsonSerializer.SerializeToElement(result, WeatherJsonContext.Default.WeatherSetCityResult);
    }
}
