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
    /// Returns an emoji for the given WMO weather code.
    /// </summary>
    public static string GetEmoji(int code, bool isDay = true)
    {
        return code switch
        {
            0 => isDay ? "\U0001F31E" : "\U0001F319",       // ☀️ Clear sky day / 🌙 night
            1 => isDay ? "\U0001F31E" : "\U0001F319",       // Mainly clear
            2 => isDay ? "\u26C5" : "\U0001F319",           // ⛅ Partly cloudy / 🌙
            3 => "\U0001F325\uFE0F",                          // 🌥️ Overcast
            45 => "\U0001F32B\uFE0F",                         // 🌫️ Fog
            48 => "\U0001F32B\uFE0F",                         // 🌫️ Depositing rime fog
            51 => "\U0001F326\uFE0F",                         // 🌦️ Light drizzle
            53 => "\U0001F326\uFE0F",                         // 🌦️ Moderate drizzle
            55 => "\U0001F326\uFE0F",                         // 🌦️ Dense drizzle
            56 => "\U0001F326\uFE0F",                         // 🌦️ Light freezing drizzle
            57 => "\U0001F326\uFE0F",                         // 🌦️ Dense freezing drizzle
            61 => "\U0001F327\uFE0F",                         // 🌧️ Slight rain
            63 => "\U0001F327\uFE0F",                         // 🌧️ Moderate rain
            65 => "\U0001F327\uFE0F",                         // 🌧️ Heavy rain
            66 => "\U0001F327\uFE0F",                         // 🌧️ Light freezing rain
            67 => "\U0001F327\uFE0F",                         // 🌧️ Heavy freezing rain
            71 => "\U0001F328\uFE0F",                         // 🌨️ Slight snow fall
            73 => "\U0001F328\uFE0F",                         // 🌨️ Moderate snow fall
            75 => "\U0001F328\uFE0F",                         // 🌨️ Heavy snow fall
            77 => "\U0001F328\uFE0F",                         // 🌨️ Snow grains
            80 => "\U0001F326\uFE0F",                         // 🌦️ Slight rain showers
            81 => "\U0001F327\uFE0F",                         // 🌧️ Moderate rain showers
            82 => "\U0001F327\uFE0F",                         // 🌧️ Violent rain showers
            85 => "\U0001F328\uFE0F",                         // 🌨️ Slight snow showers
            86 => "\U0001F328\uFE0F",                         // 🌨️ Heavy snow showers
            95 => "\U0001F329\uFE0F",                         // 🌩️ Thunderstorm
            96 => "\u26C8\uFE0F",                             // ⛈️ Thunderstorm with slight hail
            99 => "\u26C8\uFE0F",                             // ⛈️ Thunderstorm with heavy hail
            _ => "\U0001F31E"                                  // ☀️ Unknown → sun
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
    /// Returns the Chinese description for the given WMO weather code.
    /// </summary>
    public static string GetDescriptionZh(int code)
    {
        return code switch
        {
            0 => "晴",
            1 => "晴间多云",
            2 => "多云",
            3 => "阴",
            45 => "雾",
            48 => "冻雾",
            51 => "小雨",
            53 => "小雨",
            55 => "中雨",
            56 => "冻雨",
            57 => "冻雨",
            61 => "小雨",
            63 => "中雨",
            65 => "大雨",
            66 => "冻雨",
            67 => "冻雨",
            71 => "小雪",
            73 => "中雪",
            75 => "大雪",
            77 => "米雪",
            80 => "阵雨",
            81 => "阵雨",
            82 => "强阵雨",
            85 => "阵雪",
            86 => "强阵雪",
            95 => "雷阵雨",
            96 => "雷阵雨伴冰雹",
            99 => "雷阵雨伴大冰雹",
            _ => "未知"
        };
    }

    /// <summary>
    /// Returns the English description for the given WMO weather code.
    /// </summary>
    public static string GetDescriptionEn(int code)
    {
        return code switch
        {
            0 => "Clear sky",
            1 => "Mainly clear",
            2 => "Partly cloudy",
            3 => "Overcast",
            45 => "Fog",
            48 => "Rime fog",
            51 => "Light rain",
            53 => "Light rain",
            55 => "Moderate rain",
            56 => "Freezing rain",
            57 => "Freezing rain",
            61 => "Light rain",
            63 => "Moderate rain",
            65 => "Heavy rain",
            66 => "Freezing rain",
            67 => "Freezing rain",
            71 => "Light snow",
            73 => "Moderate snow",
            75 => "Heavy snow",
            77 => "Snow grains",
            80 => "Rain showers",
            81 => "Rain showers",
            82 => "Heavy rain showers",
            85 => "Snow showers",
            86 => "Heavy snow showers",
            95 => "Thundershowers",
            96 => "Thundershowers with hail",
            99 => "Thundershowers with heavy hail",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Returns the localized description for the given WMO weather code.
    /// </summary>
    public static string GetDescription(int code, string language)
    {
        return language switch
        {
            "zh-CN" => GetDescriptionZh(code),
            "ja-JP" => GetDescriptionJa(code),
            "de-DE" => GetDescriptionDe(code),
            "pt-BR" => GetDescriptionPt(code),
            _ => GetDescriptionEn(code)
        };
    }

    private static string GetDescriptionJa(int code)
    {
        return code switch
        {
            0 => "晴天",
            1 => "ほぼ晴れ",
            2 => "曇りがち",
            3 => "曇り",
            45 => "霧",
            48 => "着氷霧",
            51 => "弱い雨",
            53 => "弱い雨",
            55 => "雨",
            56 => "着氷雨",
            57 => "着氷雨",
            61 => "弱い雨",
            63 => "雨",
            65 => "強い雨",
            66 => "着氷雨",
            67 => "着氷雨",
            71 => "弱い雪",
            73 => "雪",
            75 => "強い雪",
            77 => "霧雪",
            80 => "にわか雨",
            81 => "にわか雨",
            82 => "強いにわか雨",
            85 => "にわか雪",
            86 => "強いにわか雪",
            95 => "雷雨",
            96 => "雹を伴う雷雨",
            99 => "激しい雹を伴う雷雨",
            _ => "不明"
        };
    }

    private static string GetDescriptionDe(int code)
    {
        return code switch
        {
            0 => "Klar",
            1 => "Überwiegend klar",
            2 => "Teilweise bewölkt",
            3 => "Bedeckt",
            45 => "Nebel",
            48 => "Reifnebel",
            51 => "Leichter Regen",
            53 => "Leichter Regen",
            55 => "Mäßiger Regen",
            56 => "Gefrierender Regen",
            57 => "Gefrierender Regen",
            61 => "Leichter Regen",
            63 => "Mäßiger Regen",
            65 => "Starker Regen",
            66 => "Gefrierender Regen",
            67 => "Gefrierender Regen",
            71 => "Leichter Schnee",
            73 => "Mäßiger Schnee",
            75 => "Starker Schnee",
            77 => "Schneegriesel",
            80 => "Regenschauer",
            81 => "Regenschauer",
            82 => "Starke Regenschauer",
            85 => "Schneeschauer",
            86 => "Starke Schneeschauer",
            95 => "Gewitter",
            96 => "Gewitter mit Hagel",
            99 => "Gewitter mit starkem Hagel",
            _ => "Unbekannt"
        };
    }

    private static string GetDescriptionPt(int code)
    {
        return code switch
        {
            0 => "Céu limpo",
            1 => "Predominantemente limpo",
            2 => "Parcialmente nublado",
            3 => "Nublado",
            45 => "Nevoeiro",
            48 => "Nevoeiro com geada",
            51 => "Chuva fraca",
            53 => "Chuva fraca",
            55 => "Chuva moderada",
            56 => "Chuva congelante",
            57 => "Chuva congelante",
            61 => "Chuva fraca",
            63 => "Chuva moderada",
            65 => "Chuva forte",
            66 => "Chuva congelante",
            67 => "Chuva congelante",
            71 => "Neve fraca",
            73 => "Neve moderada",
            75 => "Neve forte",
            77 => "Grãos de neve",
            80 => "Pancadas de chuva",
            81 => "Pancadas de chuva",
            82 => "Pancadas de chuva fortes",
            85 => "Pancadas de neve",
            86 => "Pancadas de neve fortes",
            95 => "Trovoada",
            96 => "Trovoada com granizo",
            99 => "Trovoada com granizo forte",
            _ => "Desconhecido"
        };
    }
}
