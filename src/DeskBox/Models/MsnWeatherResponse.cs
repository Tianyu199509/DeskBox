using System.Text.Json.Serialization;

namespace DeskBox.Models;

/// <summary>
/// Top-level MSN Weather API response.
/// Endpoint: https://api.msn.com/weather/overview
/// </summary>
public sealed class MsnWeatherResponse
{
    [JsonPropertyName("value")]
    public List<MsnWeatherValue>? Value { get; set; }
}

public sealed class MsnWeatherValue
{
    [JsonPropertyName("responses")]
    public List<MsnWeatherResponses>? Responses { get; set; }
}

public sealed class MsnWeatherResponses
{
    [JsonPropertyName("weather")]
    public List<MsnWeatherBody>? Weather { get; set; }
}

public sealed class MsnWeatherBody
{
    [JsonPropertyName("current")]
    public MsnWeatherCurrent? Current { get; set; }

    [JsonPropertyName("forecast")]
    public MsnWeatherForecast? Forecast { get; set; }
}

/// <summary>
/// MSN current weather snapshot.
/// </summary>
public sealed class MsnWeatherCurrent
{
    [JsonPropertyName("temp")]
    public double Temp { get; set; }

    [JsonPropertyName("feels")]
    public double Feels { get; set; }

    [JsonPropertyName("rh")]
    public double Rh { get; set; }

    [JsonPropertyName("windSpd")]
    public double WindSpd { get; set; }

    [JsonPropertyName("windDir")]
    public double WindDir { get; set; }

    [JsonPropertyName("baro")]
    public double Baro { get; set; }

    [JsonPropertyName("cap")]
    public string Cap { get; set; } = string.Empty;

    [JsonPropertyName("pvdrCap")]
    public string PvdrCap { get; set; } = string.Empty;

    [JsonPropertyName("daytime")]
    public string Daytime { get; set; } = "d";

    [JsonPropertyName("uv")]
    public double Uv { get; set; }

    [JsonPropertyName("uvDesc")]
    public string UvDesc { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public int Icon { get; set; }

    [JsonPropertyName("vis")]
    public double Vis { get; set; }

    [JsonPropertyName("dewPt")]
    public double DewPt { get; set; }

    [JsonPropertyName("cloudCover")]
    public double CloudCover { get; set; }

    [JsonPropertyName("aqi")]
    public double Aqi { get; set; }

    [JsonPropertyName("created")]
    public string Created { get; set; } = string.Empty;

    /// <summary>True when daytime is "d".</summary>
    [JsonIgnore]
    public bool IsDay => string.Equals(Daytime, "d", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// MSN forecast container with daily entries.
/// </summary>
public sealed class MsnWeatherForecast
{
    [JsonPropertyName("days")]
    public List<MsnForecastDay>? Days { get; set; }
}

/// <summary>
/// One day of forecast: hourly slots, daily summary, and almanac.
/// </summary>
public sealed class MsnForecastDay
{
    [JsonPropertyName("hourly")]
    public List<MsnForecastHour>? Hourly { get; set; }

    [JsonPropertyName("daily")]
    public MsnDailySummary? Daily { get; set; }

    [JsonPropertyName("almanac")]
    public MsnAlmanac? Almanac { get; set; }
}

public sealed class MsnForecastHour
{
    [JsonPropertyName("valid")]
    public string Valid { get; set; } = string.Empty;

    [JsonPropertyName("temp")]
    public double Temp { get; set; }

    [JsonPropertyName("cap")]
    public string Cap { get; set; } = string.Empty;

    [JsonPropertyName("precip")]
    public double Precip { get; set; }

    [JsonPropertyName("icon")]
    public int Icon { get; set; }

    [JsonPropertyName("windSpd")]
    public double WindSpd { get; set; }

    [JsonPropertyName("windDir")]
    public double WindDir { get; set; }

    [JsonPropertyName("rh")]
    public double Rh { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Daily summary with high/low temps and UV.
/// </summary>
public sealed class MsnDailySummary
{
    [JsonPropertyName("valid")]
    public string Valid { get; set; } = string.Empty;

    [JsonPropertyName("tempHi")]
    public double TempHi { get; set; }

    [JsonPropertyName("tempLo")]
    public double TempLo { get; set; }

    [JsonPropertyName("uv")]
    public double Uv { get; set; }

    [JsonPropertyName("pvdrCap")]
    public string PvdrCap { get; set; } = string.Empty;

    [JsonPropertyName("day")]
    public MsnDayNightPeriod? Day { get; set; }

    [JsonPropertyName("night")]
    public MsnDayNightPeriod? Night { get; set; }
}

public sealed class MsnDayNightPeriod
{
    [JsonPropertyName("cap")]
    public string Cap { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public int Icon { get; set; }

    [JsonPropertyName("precip")]
    public double Precip { get; set; }

    [JsonPropertyName("windSpd")]
    public double WindSpd { get; set; }

    [JsonPropertyName("windDir")]
    public double WindDir { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Astronomical data: sunrise/sunset/moonrise/moonset.
/// </summary>
public sealed class MsnAlmanac
{
    [JsonPropertyName("valid")]
    public string Valid { get; set; } = string.Empty;

    [JsonPropertyName("sunrise")]
    public string Sunrise { get; set; } = string.Empty;

    [JsonPropertyName("sunset")]
    public string Sunset { get; set; } = string.Empty;

    [JsonPropertyName("moonrise")]
    public string Moonrise { get; set; } = string.Empty;

    [JsonPropertyName("moonset")]
    public string Moonset { get; set; } = string.Empty;

    [JsonPropertyName("moonPhase")]
    public string MoonPhase { get; set; } = string.Empty;
}
