using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace DeskBox.Models;

/// <summary>
/// Complete weather data payload consumed by the weather widget.
/// </summary>
public sealed class WeatherData
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("timezone")]
    public string Timezone { get; set; } = string.Empty;

    [JsonPropertyName("current")]
    public WeatherCurrent? Current { get; set; }

    [JsonPropertyName("daily")]
    public WeatherDaily? Daily { get; set; }

    [JsonPropertyName("hourly")]
    public WeatherHourly? Hourly { get; set; }

    /// <summary>Display name resolved from geocoding or user input.</summary>
    [JsonIgnore]
    public string LocationName { get; set; } = string.Empty;
}

public sealed class WeatherCurrent
{
    [JsonPropertyName("time")]
    public string Time { get; set; } = string.Empty;

    [JsonPropertyName("temperature_2m")]
    public double Temperature { get; set; }

    [JsonPropertyName("relative_humidity_2m")]
    public double Humidity { get; set; }

    [JsonPropertyName("apparent_temperature")]
    public double ApparentTemperature { get; set; }

    [JsonPropertyName("weather_code")]
    public int WeatherCode { get; set; }

    [JsonPropertyName("wind_speed_10m")]
    public double WindSpeed { get; set; }

    [JsonPropertyName("wind_direction_10m")]
    public double WindDirection { get; set; }

    [JsonPropertyName("pressure_msl")]
    public double Pressure { get; set; }

    [JsonPropertyName("is_day")]
    public int IsDay { get; set; } = 1;
}

public sealed class WeatherDaily
{
    [JsonPropertyName("time")]
    public List<string> Time { get; set; } = [];

    [JsonPropertyName("weather_code")]
    public List<int> WeatherCode { get; set; } = [];

    [JsonPropertyName("temperature_2m_max")]
    public List<double> TemperatureMax { get; set; } = [];

    [JsonPropertyName("temperature_2m_min")]
    public List<double> TemperatureMin { get; set; } = [];

    [JsonPropertyName("precipitation_probability_max")]
    public List<double> PrecipitationProbabilityMax { get; set; } = [];

    [JsonPropertyName("sunrise")]
    public List<string> Sunrise { get; set; } = [];

    [JsonPropertyName("sunset")]
    public List<string> Sunset { get; set; } = [];

    [JsonPropertyName("uv_index_max")]
    public List<double> UvIndexMax { get; set; } = [];
}

public sealed class WeatherHourly
{
    [JsonPropertyName("time")]
    public List<string> Time { get; set; } = [];

    [JsonPropertyName("temperature_2m")]
    public List<double> Temperature { get; set; } = [];

    [JsonPropertyName("precipitation_probability")]
    public List<double> PrecipitationProbability { get; set; } = [];

    [JsonPropertyName("weather_code")]
    public List<int> WeatherCode { get; set; } = [];
}

/// <summary>
/// Geocoding result from Open-Meteo's geocoding API.
/// </summary>
public sealed class WeatherGeocodingResult
{
    [JsonPropertyName("results")]
    public List<WeatherGeocodingItem>? Results { get; set; }
}

public sealed class WeatherGeocodingItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("admin1")]
    public string Admin1 { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Admin1)
        ? $"{Name}, {Country}"
        : $"{Name}, {Admin1}, {Country}";
}

/// <summary>
/// A simplified city search result for UI binding.
/// </summary>
public sealed class WeatherCitySearchResult
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Country { get; set; } = string.Empty;
    public string Admin1 { get; set; } = string.Empty;

    /// <summary>
    /// Returns Visible when Admin1 has content and differs from Name.
    /// </summary>
    public Visibility Admin1Visibility =>
        !string.IsNullOrEmpty(Admin1) && Admin1 != Name
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Secondary display line: "Admin1, Country" or just "Country".
    /// </summary>
    public string RegionDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Admin1) && Admin1 != Name) parts.Add(Admin1);
            if (!string.IsNullOrEmpty(Country)) parts.Add(Country);
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Used by AutoSuggestBox when a suggestion is chosen (it calls ToString()
    /// to populate the text box). Returning DisplayName ensures the correct
    /// city name is shown instead of the fully qualified type name.
    /// </summary>
    public override string ToString() => DisplayName;
}
