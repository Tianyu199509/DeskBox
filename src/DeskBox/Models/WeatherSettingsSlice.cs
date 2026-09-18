namespace DeskBox.Models;

/// <summary>
/// Weather widget location, units, source, and displayed metrics.
/// </summary>
public sealed class WeatherSettingsSlice
{
    // ─── Weather Widget Settings ───────────────────────────────────
    /// <summary>
    /// Whether the weather widget uses auto location (IP-based).
    /// </summary>
    public bool WeatherAutoLocation { get; set; } = true;

    /// <summary>
    /// Saved city name for manual location.
    /// </summary>
    public string WeatherCityName { get; set; } = string.Empty;

    /// <summary>
    /// Saved latitude for manual location.
    /// </summary>
    public double WeatherLatitude { get; set; }

    /// <summary>
    /// Saved longitude for manual location.
    /// </summary>
    public double WeatherLongitude { get; set; }

    /// <summary>
    /// Temperature unit. Valid values: <c>"Celsius"</c>, <c>"Fahrenheit"</c>.
    /// </summary>
    public string WeatherTemperatureUnit { get; set; } = "Celsius";

    /// <summary>
    /// Wind speed unit. Valid values: <c>"kmh"</c>, <c>"ms"</c>, <c>"mph"</c>.
    /// </summary>
    public string WeatherWindSpeedUnit { get; set; } = "kmh";

    /// <summary>
    /// Weather data source. Valid values: <c>"MSN"</c>, <c>"OpenMeteo"</c>.
    /// </summary>
    public string WeatherDataSource { get; set; } = "MSN";

    /// <summary>
    /// Default view mode for the weather widget. Valid values: <c>"Today"</c>, <c>"Week"</c>.
    /// </summary>
    public string WeatherDefaultView { get; set; } = "Today";

    /// <summary>
    /// Weather widget skin/theme. Valid values: <c>"Standard"</c>, <c>"Rich"</c>.
    /// </summary>
    public string WeatherSkin { get; set; } = "Standard";

    /// <summary>
    /// Whether to show the 7-day forecast in the widget.
    /// </summary>
    public bool WeatherShowForecast { get; set; } = true;

    /// <summary>
    /// Whether to show sunrise/sunset times.
    /// </summary>
    public bool WeatherShowSunrise { get; set; } = true;

    /// <summary>
    /// Whether to show UV index.
    /// </summary>
    public bool WeatherShowUvIndex { get; set; } = true;

    /// <summary>
    /// Whether to show precipitation probability.
    /// </summary>
    public bool WeatherShowPrecipitation { get; set; } = true;

    /// <summary>
    /// Whether to show humidity.
    /// </summary>
    public bool WeatherShowHumidity { get; set; } = true;

    /// <summary>
    /// Whether to show wind speed.
    /// </summary>
    public bool WeatherShowWind { get; set; } = true;

    /// <summary>
    /// Whether to show atmospheric pressure.
    /// </summary>
    public bool WeatherShowPressure { get; set; }

    /// <summary>
    /// Refresh interval in minutes. Valid values: 15, 30, 60, 180.
    /// </summary>
    public int WeatherRefreshIntervalMinutes { get; set; } = 60;
}
