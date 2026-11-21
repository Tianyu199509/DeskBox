#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Models;

namespace DeskBox.Services;

internal static class AotWeatherSurfaceFixture
{
    internal const string Scenario = "WeatherSurfacePersistenceRestart";
    internal const string OwnedWidgetId = "aot-5b4b2c2b-weather";
    internal const string LocationName = "Shanghai AOT Surface";
    internal const double Latitude = 31.2304;
    internal const double Longitude = 121.4737;

    private const string ScenarioEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_SMOKE";
    private const string PhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_WEATHER_SURFACE_PHASE";
    private static int s_requestCount;

    internal static WeatherService? TryCreateService(WidgetConfig config)
    {
        string? scenario = Environment.GetEnvironmentVariable(
            ScenarioEnvironmentVariable);
        string? phase = Environment.GetEnvironmentVariable(
            PhaseEnvironmentVariable);
        if (!string.Equals(scenario, Scenario, StringComparison.Ordinal) ||
            !string.Equals(config.Id, OwnedWidgetId, StringComparison.Ordinal) ||
            phase is not "Mutate" and not "VerifyRestore" and not "Postflight")
        {
            return null;
        }

        return new WeatherService(CreateData);
    }

    private static WeatherData CreateData(
        double latitude,
        double longitude,
        string locationName)
    {
        if (Math.Abs(latitude - Latitude) > 0.000001 ||
            Math.Abs(longitude - Longitude) > 0.000001 ||
            !string.Equals(locationName, LocationName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The NativeAOT Weather surface requested data outside its owned fixture.");
        }

        int requestCount = Interlocked.Increment(ref s_requestCount);
        App.Log(
            $"[AotWeatherSurfaceFixture] Served deterministic WeatherData request " +
            $"#{requestCount} for '{OwnedWidgetId}'.");
        return CreateDeterministicData();
    }

    private static WeatherData CreateDeterministicData()
    {
        string[] dates =
        [
            "2030-01-02",
            "2030-01-03",
            "2030-01-04",
            "2030-01-05",
            "2030-01-06",
            "2030-01-07",
            "2030-01-08"
        ];
        var hourlyTimes = new List<string>(24);
        var hourlyTemperatures = new List<double>(24);
        var hourlyPrecipitation = new List<double>(24);
        var hourlyCodes = new List<int>(24);
        int[] forecastCodes = [61, 3, 0, 2, 71, 95, 45];
        for (int hour = 0; hour < 24; hour++)
        {
            hourlyTimes.Add($"2030-01-02T{hour:00}:00");
            hourlyTemperatures.Add(20 + (hour % 6));
            hourlyPrecipitation.Add((hour * 7) % 100);
            hourlyCodes.Add(forecastCodes[hour % forecastCodes.Length]);
        }

        return new WeatherData
        {
            Latitude = Latitude,
            Longitude = Longitude,
            Timezone = "Asia/Shanghai",
            LocationName = LocationName,
            Current = new WeatherCurrent
            {
                Time = "2030-01-02T09:00",
                Temperature = 20,
                Humidity = 64,
                ApparentTemperature = 19,
                WeatherCode = 61,
                WindSpeed = 18,
                WindDirection = 90,
                Pressure = 1012,
                IsDay = 1
            },
            Daily = new WeatherDaily
            {
                Time = [.. dates],
                WeatherCode = [61, 3, 0, 2, 71, 95, 45],
                TemperatureMax = [24, 23, 26, 25, 8, 18, 20],
                TemperatureMin = [16, 15, 17, 18, -1, 10, 12],
                PrecipitationProbabilityMax = [70, 20, 0, 10, 80, 90, 15],
                Sunrise =
                [
                    "2030-01-02T06:15",
                    "2030-01-03T06:16",
                    "2030-01-04T06:16",
                    "2030-01-05T06:17",
                    "2030-01-06T06:17",
                    "2030-01-07T06:18",
                    "2030-01-08T06:18"
                ],
                Sunset =
                [
                    "2030-01-02T18:42",
                    "2030-01-03T18:43",
                    "2030-01-04T18:43",
                    "2030-01-05T18:44",
                    "2030-01-06T18:45",
                    "2030-01-07T18:45",
                    "2030-01-08T18:46"
                ],
                UvIndexMax = [5, 4, 6, 5, 2, 3, 4]
            },
            Hourly = new WeatherHourly
            {
                Time = hourlyTimes,
                Temperature = hourlyTemperatures,
                PrecipitationProbability = hourlyPrecipitation,
                WeatherCode = hourlyCodes
            }
        };
    }
}
#endif
