
using DeskBox.Contracts;
using DeskBox.Services;
namespace DeskBox.Helpers;

/// <summary>
/// Maps WMO weather interpretation codes to localized descriptions, emoji icons, and
/// weather condition categories for animation effects.
/// Reference: https://open-meteo.com/en/docs (WMO Weather interpretation codes)
/// </summary>
public static class WeatherCodeMapper
{
    /// <summary>
    /// Weather condition category, used to drive skin animations.
    /// </summary>
    public enum WeatherCondition
    {
        Clear,
        Cloudy,
        Fog,
        Drizzle,
        Rain,
        Snow,
        Thunderstorm,
        Unknown
    }

    /// <summary>
    /// Returns the bundled weather icon URI for the given WMO code and icon
    /// style. Bundled SVGs render identically on Windows 10 and 11.
    /// <c>DeskBox</c> icons live at Assets/WeatherIcons/*.svg;
    /// the bundled Meteocons styles (Flat / Fill / Line) live under
    /// Assets/WeatherIcons/{style}/ with their upstream file names; the
    /// Fluent style (Microsoft Fluent Emoji Color, MIT) lives under
    /// Assets/WeatherIcons/fluent/ using the DeskBox file names. The style
    /// match is case-insensitive, and anything that is not a known bundled
    /// style (Emoji or an unknown value) falls back to the DeskBox artwork
    /// so callers stay crash-safe. This render-side fallback contract
    /// intentionally differs from the settings layer's unknown→Flat
    /// persistence default: the icon combo always writes canonical values,
    /// so an unknown value here means stale or hand-edited state, where the
    /// guaranteed-bundled root set is the safe pick.
    /// </summary>
    public static Uri GetIconUri(int code, bool isDay = true, string? style = null)
    {
        bool meteocons = style is not null &&
            (string.Equals(style, WeatherOptionKinds.IconStyleFlat, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(style, WeatherOptionKinds.IconStyleLine, StringComparison.OrdinalIgnoreCase));
        bool fluent = style is not null &&
            string.Equals(style, WeatherOptionKinds.IconStyleFluent, StringComparison.OrdinalIgnoreCase);
        string name = meteocons
            ? GetMeteoconsIconName(code, isDay)
            : GetDeskBoxIconName(code, isDay);
        string subdir = meteocons
            ? style!.ToLowerInvariant() + "/"
            : fluent ? "fluent/" : string.Empty;
        return new Uri($"ms-appx:///Assets/WeatherIcons/{subdir}{name}.svg");
    }

    /// <summary>File names of the bundled Meteocons styles (flat/fill/line).</summary>
    private static string GetMeteoconsIconName(int code, bool isDay)
    {
        return code switch
        {
            0 or 1 => isDay ? "clear-day" : "clear-night",
            2 => isDay ? "partly-cloudy-day" : "partly-cloudy-night",
            3 => "overcast",
            45 or 48 => isDay ? "fog-day" : "fog-night",
            >= 51 and <= 57 => "drizzle",
            66 or 67 => "sleet",
            >= 61 and <= 65 => "rain",
            >= 71 and <= 77 => "snow",
            80 => isDay ? "partly-cloudy-day-rain" : "partly-cloudy-night-rain",
            81 or 82 => "rain",
            85 or 86 => isDay ? "partly-cloudy-day-snow" : "partly-cloudy-night-snow",
            95 => "thunderstorms",
            96 or 99 => "thunderstorms-hail",
            _ => isDay ? "clear-day" : "clear-night"
        };
    }

    /// <summary>File names of the in-repo hand-drawn DeskBox set.</summary>
    private static string GetDeskBoxIconName(int code, bool isDay)
    {
        return code switch
        {
            0 or 1 => isDay ? "clear-day" : "clear-night",
            2 => isDay ? "partly-cloudy-day" : "partly-cloudy-night",
            3 => "overcast",
            45 or 48 => "fog",
            >= 51 and <= 57 => "drizzle",
            66 or 67 => "sleet",
            >= 61 and <= 65 => "rain",
            >= 71 and <= 77 => "snow",
            80 => "rain-showers",
            81 or 82 => "rain",
            85 or 86 => "snow",
            95 => "thunderstorms",
            96 or 99 => "thunderstorms-hail",
            _ => isDay ? "clear-day" : "clear-night"
        };
    }

    /// <summary>
    /// Returns the weather condition category for animation purposes.
    /// </summary>
    public static WeatherCondition GetCondition(int code)
    {
        return code switch
        {
            0 or 1 => WeatherCondition.Clear,
            2 or 3 => WeatherCondition.Cloudy,
            45 or 48 => WeatherCondition.Fog,
            >= 51 and <= 57 => WeatherCondition.Drizzle,
            >= 61 and <= 67 or >= 80 and <= 82 => WeatherCondition.Rain,
            >= 71 and <= 77 or >= 85 and <= 86 => WeatherCondition.Snow,
            >= 95 and <= 99 => WeatherCondition.Thunderstorm,
            _ => WeatherCondition.Unknown
        };
    }

    // ── Reverse mapping: MSN weather description text → WMO code ──

    /// <summary>
    /// Maps a weather description string (as returned by MSN Weather API's "cap" field)
    /// to the closest WMO weather interpretation code.
    /// This allows MSN-sourced data to reuse the existing emoji/glyph/animation system
    /// that is keyed on WMO codes.
    /// </summary>
    public static int DescriptionToWmoCode(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return -1;
        }

        // Normalize: trim, lowercase for comparison
        string d = description.Trim();

        // Chinese descriptions (MSN returns these when locale is zh-CN)
        return d switch
        {
            // Clear / Sunny
            "晴" or "Sunny" or "Clear" or "Clear sky" => 0,
            "晴间多云" or "Mostly sunny" or "Mainly clear" => 1,
            "多云" or "Partly cloudy" or "Partly Sunny" => 2,
            "阴" or "Overcast" or "Cloudy" or "Mostly cloudy" or "Mostly Cloudy" => 3,

            // Fog
            "雾" or "Fog" or "Foggy" => 45,
            "冻雾" or "Freezing fog" => 48,
            "薄雾" or "Mist" or "Haze" => 45,

            // Drizzle
            "小雨" or "Light rain" or "Light drizzle" or "Drizzle" => 51,
            "毛毛雨" or "Drizzle" => 51,

            // Rain
            "中雨" or "Moderate rain" => 63,
            "大雨" or "Heavy rain" => 65,
            "暴雨" or "Torrential rain" or "Very heavy rain" => 65,
            "阵雨" or "Rain showers" or "Showers" or "Scattered showers" => 80,
            "强阵雨" or "Heavy rain showers" or "Heavy showers" => 82,

            // Freezing rain
            "冻雨" or "Freezing rain" or "Ice rain" => 66,

            // Snow
            "小雪" or "Light snow" => 71,
            "中雪" or "Moderate snow" => 73,
            "大雪" or "Heavy snow" => 75,
            "阵雪" or "Snow showers" => 85,
            "强阵雪" or "Heavy snow showers" => 86,
            "雨夹雪" or "Sleet" or "Rain and snow" => 77,
            "米雪" or "Snow grains" => 77,

            // Thunderstorm
            "雷阵雨" or "Thundershowers" or "Thunderstorm" or "Thundershower" => 95,
            "雷阵雨伴冰雹" or "Thunderstorm with hail" => 96,
            "雷阵雨伴大冰雹" or "Thunderstorm with heavy hail" => 99,
            "雷暴" or "Thunder" => 95,

            // Mixed / other
            "沙尘暴" or "Sandstorm" => 45,
            "浮尘" or "Dust" => 45,
            "扬沙" or "Sand" => 45,

            // English MSN variants (for non-zh-CN locales)
            "Mostly clear" => 1,
            "Partly Cloudy" => 2,
            "Scattered clouds" => 2,
            "Light Rain" => 61,
            "Moderate Rain" => 63,
            "Heavy Rain" => 65,
            "Light Snow" => 71,
            "Moderate Snow" => 73,
            "Heavy Snow" => 75,

            _ => -1
        };
    }

    /// <summary>
    /// Returns the best-effort WMO code from an MSN description, falling back to the
    /// MSN icon code mapping if description matching fails.
    /// MSN icon codes loosely map to WMO: 1=clear, 2-4=cloudy, 5-11=rain, 13-14=snow, etc.
    /// </summary>
    public static int MsnDescriptionOrIconToWmoCode(string description, int msnIcon)
    {
        int fromDesc = DescriptionToWmoCode(description);
        if (fromDesc >= 0)
        {
            return fromDesc;
        }

        // Fallback: MSN icon code → approximate WMO code
        return msnIcon switch
        {
            1 => 0,       // Sunny
            2 => 1,       // Mostly sunny
            3 => 2,       // Partly cloudy
            4 => 3,       // Cloudy / Overcast
            5 => 45,      // Fog
            6 => 45,      // Haze / Smoke
            7 => 51,      // Light rain
            8 => 63,      // Rain
            9 => 65,      // Heavy rain
            10 => 66,     // Freezing rain
            11 => 80,     // Rain showers
            12 => 71,     // Light snow
            13 => 73,     // Snow
            14 => 75,     // Heavy snow
            15 => 77,     // Sleet
            16 => 85,     // Snow showers
            17 => 95,     // Thunderstorm
            18 => 96,     // Thunderstorm with hail
            19 => 45,     // Blowing snow / dust
            20 => 45,     // Dust
            21 => 51,     // Mist / drizzle
            22 => 45,     // Smoke
            23 => 63,     // Windy rain
            24 => 3,      // Mostly cloudy
            25 => 45,     // Fog
            26 => 2,      // Partly cloudy (night)
            27 => 0,      // Clear (night)
            28 => 1,      // Mostly clear (night)
            29 => 29,     // Pass through for night-specific
            30 => 2,      // Partly cloudy night
            31 => 0,      // Clear night
            32 => 1,      // Mostly clear night
            33 => 2,      // Partly cloudy night
            34 => 3,      // Mostly cloudy night
            _ => -1
        };
    }

    // ── Legacy glyph support (kept for backward compatibility) ──

    /// <summary>
    /// Returns a Segoe Fluent Icons glyph for the given WMO weather code.
    /// Glyphs are chosen for visual clarity at small sizes (16-20px) and
    /// consistent rendering across Windows versions.
    /// </summary>
    public static string GetGlyph(int code, bool isDay = true)
    {
        return code switch
        {
            0 => isDay ? "\uE706" : "\uE708",   // Sun / Moon
            1 => isDay ? "\uE706" : "\uE708",   // Sun / Moon (mainly clear)
            2 => isDay ? "\uE9D2" : "\uE708",   // PartlyCloudyDay (Cloud) / Moon
            3 => "\uE9D2",                        // Cloud (overcast)
            45 => "\uE9CB",                       // Fog
            48 => "\uE9CB",                       // Fog (rime)
            51 => "\uE755",                       // Rain (light drizzle)
            53 => "\uE755",                       // Rain (moderate drizzle)
            55 => "\uE755",                       // Rain (dense drizzle)
            56 => "\uE755",                       // Rain (freezing drizzle)
            57 => "\uE755",                       // Rain (freezing drizzle)
            61 => "\uE755",                       // Rain (slight)
            63 => "\uE755",                       // Rain (moderate)
            65 => "\uE755",                       // Rain (heavy)
            66 => "\uE755",                       // Rain (freezing)
            67 => "\uE755",                       // Rain (heavy freezing)
            71 => "\uE703",                       // Snow (slight)
            73 => "\uE703",                       // Snow (moderate)
            75 => "\uE703",                       // Snow (heavy)
            77 => "\uE703",                       // Snow (grains)
            80 => "\uE755",                       // Rain (showers)
            81 => "\uE755",                       // Rain (moderate showers)
            82 => "\uE755",                       // Rain (violent showers)
            85 => "\uE703",                       // Snow (showers)
            86 => "\uE703",                       // Snow (heavy showers)
            95 => "\uE756",                       // Thunderstorm
            96 => "\uE756",                       // Thunderstorm (hail)
            99 => "\uE756",                       // Thunderstorm (heavy hail)
            _ => "\uE706"                          // Sun (unknown fallback)
        };
    }

    /// <summary>
    /// Returns the localization resource key for the given WMO weather
    /// interpretation code ("Weather.Condition.*" in Strings/{lang}.json).
    /// Unrecognized codes fall back to "Weather.Condition.Unknown".
    /// </summary>
    public static string GetDescriptionKey(int code)
    {
        return code switch
        {
            0 => "Weather.Condition.0",
            1 => "Weather.Condition.1",
            2 => "Weather.Condition.2",
            3 => "Weather.Condition.3",
            45 => "Weather.Condition.45",
            48 => "Weather.Condition.48",
            51 => "Weather.Condition.51",
            53 => "Weather.Condition.53",
            55 => "Weather.Condition.55",
            56 => "Weather.Condition.56",
            57 => "Weather.Condition.57",
            61 => "Weather.Condition.61",
            63 => "Weather.Condition.63",
            65 => "Weather.Condition.65",
            66 => "Weather.Condition.66",
            67 => "Weather.Condition.67",
            71 => "Weather.Condition.71",
            73 => "Weather.Condition.73",
            75 => "Weather.Condition.75",
            77 => "Weather.Condition.77",
            80 => "Weather.Condition.80",
            81 => "Weather.Condition.81",
            82 => "Weather.Condition.82",
            85 => "Weather.Condition.85",
            86 => "Weather.Condition.86",
            95 => "Weather.Condition.95",
            96 => "Weather.Condition.96",
            99 => "Weather.Condition.99",
            _ => "Weather.Condition.Unknown"
        };
    }

    /// <summary>
    /// Returns the localized description for the given WMO weather interpretation
    /// code in the current UI language. The wording lives in the shared
    /// Strings/{lang}.json resources and is resolved through
    /// <see cref="LocalizationService"/>, so no language word table is compiled
    /// into this mapper.
    /// </summary>
    public static string GetDescription(int code, LocalizationService localizationService)
    {
        ArgumentNullException.ThrowIfNull(localizationService);
        return localizationService.T(GetDescriptionKey(code));
    }
}
