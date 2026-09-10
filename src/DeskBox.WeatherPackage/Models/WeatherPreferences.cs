using System.Text.Json.Serialization;

namespace DeskBox.WeatherPackage.Models;

/// <summary>The existing seventeen feature-wide preferences, with unchanged defaults.</summary>
public sealed class WeatherPreferences
{
    public bool WeatherAutoLocation { get; set; } = true;
    public string WeatherCityName { get; set; } = string.Empty;
    public double WeatherLatitude { get; set; }
    public double WeatherLongitude { get; set; }
    public string WeatherTemperatureUnit { get; set; } = "Celsius";
    public string WeatherWindSpeedUnit { get; set; } = "kmh";
    public string WeatherDataSource { get; set; } = "MSN";
    public string WeatherDefaultView { get; set; } = "Today";
    public string WeatherSkin { get; set; } = "Standard";
    public bool WeatherShowForecast { get; set; } = true;
    public bool WeatherShowSunrise { get; set; } = true;
    public bool WeatherShowUvIndex { get; set; } = true;
    public bool WeatherShowPrecipitation { get; set; } = true;
    public bool WeatherShowHumidity { get; set; } = true;
    public bool WeatherShowWind { get; set; } = true;
    public bool WeatherShowPressure { get; set; }
    public int WeatherRefreshIntervalMinutes { get; set; } = 60;
    [JsonIgnore] public double TextSize { get; set; } = 11.5;
}

public sealed class WeatherInstanceConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefaultTitle { get; set; } = true;
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public sealed class WeatherConfigSnapshot
{
    public int Version { get; set; } = 1;
    public string LastRequestId { get; set; } = string.Empty;
    public bool LastWriteSucceeded { get; set; } = true;
    public System.Text.Json.JsonElement Environment { get; set; }
    public WeatherPreferences Settings { get; set; } = new();
    public WeatherInstanceConfig Instance { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(WeatherConfigSnapshot))]
[JsonSerializable(typeof(WeatherPreferences))]
[JsonSerializable(typeof(WeatherInstanceConfig))]
internal partial class WeatherConfigJsonContext : JsonSerializerContext;
