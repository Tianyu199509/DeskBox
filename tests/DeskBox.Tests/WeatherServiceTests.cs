using System.Globalization;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class WeatherServiceTests
{
    [Fact]
    public void SourceGeneratedParsers_KeepCaseInsensitiveAndUnknownFieldBehavior()
    {
        WeatherGeocodingResult geocoding = Assert.IsType<WeatherGeocodingResult>(
            WeatherService.DeserializeGeocodingResponse(
                """
                {
                  "RESULTS": [
                    {
                      "NAME": "Paris",
                      "LATITUDE": 48.85,
                      "LONGITUDE": 2.35,
                      "COUNTRY": "France",
                      "futureCityField": true
                    }
                  ],
                  "futureRootField": "ignored"
                }
                """));
        WeatherGeocodingItem city = Assert.Single(geocoding.Results!);
        Assert.Equal("Paris", city.Name);
        Assert.Equal(48.85, city.Latitude);

        WeatherData openMeteo = Assert.IsType<WeatherData>(
            WeatherService.DeserializeOpenMeteoResponse(
                """
                {
                  "LATITUDE": 1.5,
                  "LONGITUDE": 2.5,
                  "CURRENT": {
                    "TEMPERATURE_2M": 23,
                    "futureCurrentField": 1
                  }
                }
                """));
        Assert.Equal(1.5, openMeteo.Latitude);
        Assert.Equal(23, Assert.IsType<WeatherCurrent>(openMeteo.Current).Temperature);

        MsnWeatherResponse msn = Assert.IsType<MsnWeatherResponse>(
            WeatherService.DeserializeMsnResponse(
                """
                {
                  "VALUE": [
                    {
                      "RESPONSES": [
                        {
                          "WEATHER": [
                            {
                              "CURRENT": {
                                "TEMP": 27,
                                "CAP": "Clear",
                                "futureMsnField": false
                              }
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """));
        MsnWeatherCurrent current = Assert.IsType<MsnWeatherCurrent>(
            Assert.Single(
                Assert.Single(
                    Assert.Single(msn.Value!).Responses!).Weather!).Current);
        Assert.Equal(27, current.Temp);
        Assert.Equal("Clear", current.Cap);
    }

    [Fact]
    public void ConvertMsnToWeatherData_UsesFollowingDayWhenCurrentDayHasNoHourlyRows()
    {
        var response = new MsnWeatherBody
        {
            Forecast = new MsnWeatherForecast
            {
                Days =
                [
                    new MsnForecastDay { Hourly = [] },
                    new MsnForecastDay
                    {
                        Hourly =
                        [
                            new MsnForecastHour
                            {
                                Valid = "2026-07-29T00:00:00+08:00",
                                Temp = 27,
                                Precip = 20,
                                Cap = "Partly cloudy",
                                Icon = 29
                            },
                            new MsnForecastHour
                            {
                                Valid = "2026-07-29T01:00:00+08:00",
                                Temp = 26,
                                Precip = 30,
                                Cap = "Cloudy",
                                Icon = 26
                            }
                        ]
                    }
                ]
            }
        };

        WeatherData data = WeatherService.ConvertMsnToWeatherData(response, 30.5, 114.3);

        Assert.NotNull(data.Hourly);
        Assert.Equal(2, data.Hourly.Time.Count);
        Assert.Equal([27d, 26d], data.Hourly.Temperature);
        Assert.Equal([20d, 30d], data.Hourly.PrecipitationProbability);
        Assert.Equal(8 * 3600, data.UtcOffsetSeconds);
    }

    [Fact]
    public void DeserializeOpenMeteoResponse_PreservesUtcOffsetSeconds()
    {
        WeatherData? parsed = WeatherService.DeserializeOpenMeteoResponse(
            """
            {
              "latitude": 35.6895,
              "longitude": 139.6917,
              "timezone": "Asia/Tokyo",
              "utc_offset_seconds": 32400,
              "current": {
                "temperature_2m": 18.5,
                "weather_code": 1,
                "is_day": 1
              }
            }
            """);

        Assert.NotNull(parsed);
        Assert.Equal("Asia/Tokyo", parsed.Timezone);
        Assert.Equal(32400, parsed.UtcOffsetSeconds);
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("fi-FI")]
    public void ConvertMsnToWeatherData_FormatsGregorianIsoTimestampsAndCityOffsetAcrossNonGregorianCultures(string cultureName)
    {
        CultureInfo prevCulture = CultureInfo.CurrentCulture;
        CultureInfo prevUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            var msn = new MsnWeatherBody
            {
                Current = new MsnWeatherCurrent
                {
                    Created = "2026-11-18T18:25:00Z",
                    Temp = 12,
                    Cap = "Clear",
                    Daytime = "d"
                },
                Forecast = new MsnWeatherForecast
                {
                    Days =
                    [
                        new MsnForecastDay
                        {
                            Daily = new MsnDailySummary
                            {
                                Valid = "2026-11-18T00:00:00-05:00",
                                TempHi = 15,
                                TempLo = 7
                            },
                            Almanac = new MsnAlmanac
                            {
                                Sunrise = "2026-11-18T06:48:00-05:00",
                                Sunset = "2026-11-18T16:35:00-05:00"
                            },
                            Hourly =
                            [
                                new MsnForecastHour
                                {
                                    Valid = "2026-11-18T13:00:00-05:00",
                                    Temp = 14,
                                    Precip = 10,
                                    Cap = "Clear",
                                    Icon = 1
                                }
                            ]
                        }
                    ]
                }
            };

            WeatherData data = WeatherService.ConvertMsnToWeatherData(msn, 40.7128, -74.0060);

            Assert.Equal(-5 * 3600, data.UtcOffsetSeconds);
            Assert.Equal("2026-11-18", Assert.Single(data.Daily!.Time));
            Assert.Equal("2026-11-18T06:48", Assert.Single(data.Daily.Sunrise));
            Assert.Equal("2026-11-18T16:35", Assert.Single(data.Daily.Sunset));
            Assert.Equal("2026-11-18T13:00", Assert.Single(data.Hourly!.Time));
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
            CultureInfo.CurrentUICulture = prevUiCulture;
        }
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void FindCurrentHourIndex_ResolvesTargetCityOffsetAcrossDateLineAndNonGregorianCalendars(string cultureName)
    {
        CultureInfo prevCulture = CultureInfo.CurrentCulture;
        CultureInfo prevUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            List<string> tokyoHours = Enumerable.Range(0, 24)
                .Select(h => $"2026-11-19T{h:D2}:00")
                .ToList();

            // User PC is in UTC-8 at 2026-11-18 20:00 (04:00Z on Nov 19); Tokyo (UTC+9) is at 2026-11-19 13:00.
            var userPcNow = new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.FromHours(-8));
            int tokyoIndex = WeatherWidgetViewModel.FindCurrentHourIndex(
                tokyoHours,
                utcOffsetSeconds: 9 * 3600,
                timezone: "Asia/Tokyo",
                utcNowOverride: userPcNow);
            Assert.Equal(13, tokyoIndex);

            // Multi-hour step fallback when the exact current hour slot is omitted.
            List<string> steppedHours = ["2026-11-19T12:00", "2026-11-19T15:00", "2026-11-19T18:00"];
            int steppedIndex = WeatherWidgetViewModel.FindCurrentHourIndex(
                steppedHours,
                utcOffsetSeconds: 9 * 3600,
                utcNowOverride: userPcNow);
            Assert.Equal(0, steppedIndex);

            // Upcoming-hour fallback when provider omits earlier hours of the day.
            List<string> upcomingOnlyHours = ["2026-11-19T14:00", "2026-11-19T15:00"];
            int upcomingIndex = WeatherWidgetViewModel.FindCurrentHourIndex(
                upcomingOnlyHours,
                utcOffsetSeconds: 9 * 3600,
                utcNowOverride: userPcNow);
            Assert.Equal(0, upcomingIndex);
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
            CultureInfo.CurrentUICulture = prevUiCulture;
        }
    }
}
