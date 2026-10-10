using System.Text.Json;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WeatherCodeMapperLocalizationTests
{
    private static readonly string[] SupportedLocales =
    [
        "en-US",
        "zh-CN",
        "zh-TW",
        "ja-JP",
        "de-DE",
        "pt-BR",
        "hi-IN",
        "es-ES",
        "fr-FR",
        "ar-SA",
        "bn-BD",
        "ru-RU"
    ];

    private static readonly int[] MappedCodes =
    [
        0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57, 61, 63, 65, 66, 67,
        71, 73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99
    ];

    [Fact]
    public void AllLocales_DefineEveryWeatherConditionKey()
    {
        foreach ((string locale, var table) in ReadLocaleTables())
        {
            foreach (int code in MappedCodes)
            {
                string key = $"Weather.Condition.{code}";
                Assert.True(table.ContainsKey(key), $"{locale} is missing {key}");
                Assert.False(
                    string.IsNullOrWhiteSpace(table[key]),
                    $"{locale}:{key} has an empty translation.");
            }

            Assert.True(
                table.ContainsKey("Weather.Condition.Unknown"),
                $"{locale} is missing Weather.Condition.Unknown");
        }
    }

    [Fact]
    public void AllLocales_ShareIdenticalWeatherConditionKeys()
    {
        string[] keySets = ReadLocaleTables()
            .Values
            .Select(table => string.Join(
                '|',
                table.Keys
                    .Where(key => key.StartsWith("Weather.Condition.", StringComparison.Ordinal))
                    .OrderBy(key => key, StringComparer.Ordinal)))
            .ToArray();

        Assert.NotEmpty(keySets);

        foreach (string keySet in keySets)
        {
            Assert.Equal(keySets[0], keySet);
        }
    }

    [Fact]
    public void WeatherConditionValues_ArePlainNouns_WithoutPlaceholders()
    {
        foreach ((string locale, var table) in ReadLocaleTables())
        {
            foreach ((string key, string value) in table)
            {
                if (!key.StartsWith("Weather.Condition.", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.False(
                    value.Contains('{') || value.Contains('}'),
                    $"{locale}:{key} unexpectedly contains placeholder braces: {value}");
            }
        }
    }

    [Fact]
    public void GetDescriptionKey_MapsCodesAndFallsBackToUnknown()
    {
        foreach (int code in MappedCodes)
        {
            Assert.Equal($"Weather.Condition.{code}", WeatherCodeMapper.GetDescriptionKey(code));
        }

        Assert.Equal("Weather.Condition.Unknown", WeatherCodeMapper.GetDescriptionKey(-1));
        Assert.Equal("Weather.Condition.Unknown", WeatherCodeMapper.GetDescriptionKey(4));
        Assert.Equal("Weather.Condition.Unknown", WeatherCodeMapper.GetDescriptionKey(100));
    }

    [Theory]
    [InlineData("zh-CN", 0, "晴")]
    [InlineData("zh-TW", 0, "晴")]
    [InlineData("zh-TW", 1, "晴時多雲")]
    [InlineData("zh-TW", 95, "雷陣雨")]
    [InlineData("en-US", 48, "Rime fog")]
    [InlineData("ja-JP", 80, "にわか雨")]
    [InlineData("de-DE", 65, "Starker Regen")]
    [InlineData("pt-BR", 95, "Trovoada")]
    [InlineData("hi-IN", 0, "साफ आसमान")]
    [InlineData("es-ES", 0, "Cielo despejado")]
    [InlineData("fr-FR", 0, "Ciel dégagé")]
    [InlineData("ar-SA", 0, "سماء صافية")]
    [InlineData("bn-BD", 0, "পরিষ্কার আকাশ")]
    [InlineData("ru-RU", 0, "Ясное небо")]
    public void GetDescription_ReturnsLocalizedResourceText(string locale, int code, string expected)
    {
        LocalizationService localization = TestServices.CreateLocalizationService(locale);

        Assert.Equal(expected, WeatherCodeMapper.GetDescription(code, localization));
    }

    [Fact]
    public void GetDescription_ReturnsTheResourceValue_ForEveryCodeInEveryLocale()
    {
        foreach ((string locale, var table) in ReadLocaleTables())
        {
            LocalizationService localization = TestServices.CreateLocalizationService(locale);

            foreach (int code in MappedCodes.Concat([-1, 29, 100]))
            {
                string key = WeatherCodeMapper.GetDescriptionKey(code);
                Assert.Equal(
                    table[key],
                    WeatherCodeMapper.GetDescription(code, localization));
            }
        }
    }

    [Theory]
    [InlineData(0, true, "clear-day")]
    [InlineData(0, false, "clear-night")]
    [InlineData(1, true, "clear-day")]
    [InlineData(2, true, "partly-cloudy-day")]
    [InlineData(2, false, "partly-cloudy-night")]
    [InlineData(3, true, "overcast")]
    [InlineData(45, true, "fog")]
    [InlineData(48, false, "fog")]
    [InlineData(51, true, "drizzle")]
    [InlineData(57, true, "drizzle")]
    [InlineData(61, true, "rain")]
    [InlineData(66, true, "sleet")]
    [InlineData(67, true, "sleet")]
    [InlineData(71, true, "snow")]
    [InlineData(77, true, "snow")]
    [InlineData(80, true, "rain-showers")]
    [InlineData(81, true, "rain")]
    [InlineData(85, true, "snow")]
    [InlineData(95, true, "thunderstorms")]
    [InlineData(96, true, "thunderstorms-hail")]
    [InlineData(99, true, "thunderstorms-hail")]
    [InlineData(-1, true, "clear-day")]
    [InlineData(-1, false, "clear-night")]
    public void GetIconUri_MapsWmoCodeToBundledSvg(
        int code, bool isDay, string fileName)
    {
        Assert.Equal(
            $"ms-appx:///Assets/WeatherIcons/{fileName}.svg",
            WeatherCodeMapper.GetIconUri(code, isDay, "DeskBox").ToString());
    }

    [Theory]
    [InlineData(0, true, "flat/clear-day")]
    [InlineData(0, false, "flat/clear-night")]
    [InlineData(2, true, "flat/partly-cloudy-day")]
    [InlineData(45, false, "flat/fog-night")]
    [InlineData(80, true, "flat/partly-cloudy-day-rain")]
    [InlineData(80, false, "flat/partly-cloudy-night-rain")]
    [InlineData(86, true, "flat/partly-cloudy-day-snow")]
    [InlineData(96, true, "flat/thunderstorms-hail")]
    [InlineData(3, true, "line/overcast")]
    [InlineData(-1, false, "flat/clear-night")]
    public void GetIconUri_MeteoconsStyles_UseMeteoconsNames(
        int code, bool isDay, string relativePath)
    {
        string style = relativePath.Split('/')[0];
        Assert.Equal(
            $"ms-appx:///Assets/WeatherIcons/{relativePath}.svg",
            WeatherCodeMapper.GetIconUri(code, isDay, style).ToString());
    }

    [Theory]
    [InlineData(0, true, "fluent/clear-day")]
    [InlineData(0, false, "fluent/clear-night")]
    [InlineData(2, true, "fluent/partly-cloudy-day")]
    [InlineData(2, false, "fluent/partly-cloudy-night")]
    [InlineData(3, true, "fluent/overcast")]
    [InlineData(45, true, "fluent/fog")]
    [InlineData(51, true, "fluent/drizzle")]
    [InlineData(61, true, "fluent/rain")]
    [InlineData(66, true, "fluent/sleet")]
    [InlineData(71, true, "fluent/snow")]
    [InlineData(80, true, "fluent/rain-showers")]
    [InlineData(95, true, "fluent/thunderstorms")]
    [InlineData(96, true, "fluent/thunderstorms-hail")]
    [InlineData(-1, false, "fluent/clear-night")]
    public void GetIconUri_FluentStyle_UsesFluentSubdirWithDeskBoxNames(
        int code, bool isDay, string relativePath)
    {
        Assert.Equal(
            $"ms-appx:///Assets/WeatherIcons/{relativePath}.svg",
            WeatherCodeMapper.GetIconUri(code, isDay, "Fluent").ToString());
    }

    [Fact]
    public void GetIconUri_UnknownOrEmojiStyle_FallsBackToDeskBoxSet()
    {
        // The retired "Emoji" persisted value is now just an unknown style.
        Assert.Equal(
            "ms-appx:///Assets/WeatherIcons/clear-day.svg",
            WeatherCodeMapper.GetIconUri(0, style: "Emoji").ToString());
        Assert.Equal(
            "ms-appx:///Assets/WeatherIcons/clear-day.svg",
            WeatherCodeMapper.GetIconUri(0, style: "nonsense").ToString());
    }

    [Fact]
    public void GetIconUri_EveryMappedFile_ExistsOnDisk()
    {
        string iconsDir = Path.Combine(
            TestPaths.FromRepository("src/DeskBox/Assets/WeatherIcons"));
        foreach (string fileName in new[]
        {
            "clear-day", "clear-night", "partly-cloudy-day",
            "partly-cloudy-night", "overcast", "fog", "drizzle", "rain",
            "sleet", "snow", "rain-showers", "thunderstorms",
            "thunderstorms-hail"
        })
        {
            Assert.True(
                File.Exists(Path.Combine(iconsDir, fileName + ".svg")),
                $"Missing weather icon asset: {fileName}.svg");
            Assert.True(
                File.Exists(Path.Combine(iconsDir, "fluent", fileName + ".svg")),
                $"Missing Fluent weather icon asset: fluent/{fileName}.svg");
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ReadLocaleTables()
    {
        string stringsDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DeskBox",
            "Strings");

        return SupportedLocales.ToDictionary(
            locale => locale,
            locale => JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(stringsDirectory, locale + ".json")))
                ?? throw new InvalidDataException($"Localization file is empty: {locale}"));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "DeskBox", "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }
}
