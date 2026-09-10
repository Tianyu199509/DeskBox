extern alias WeatherPkg;
using System.Text.Json;
using DeskBox.Services;
using P = WeatherPkg::DeskBox.WeatherPackage.Services;
using M = WeatherPkg::DeskBox.WeatherPackage.Models;

namespace DeskBox.Tests;

public sealed class NativeWeatherMigrationTests
{
    [Fact]
    public void LegacySnapshotPreservesFeatureScopeAndPerInstanceView()
    {
        const string legacy = """
        {"schemaVersion":9,"weatherAutoLocation":false,"weatherCityName":"上海","weatherLatitude":31.2,"weatherLongitude":121.5,
        "weatherTemperatureUnit":"Fahrenheit","weatherWindSpeedUnit":"mph","weatherDataSource":"OpenMeteo",
        "weatherDefaultView":"Week","weatherSkin":"Rich","weatherShowForecast":false,"weatherShowSunrise":false,
        "weatherShowUvIndex":false,"weatherShowPrecipitation":false,"weatherShowHumidity":false,"weatherShowWind":false,
        "weatherShowPressure":true,"weatherRefreshIntervalMinutes":120,"todoEnabled":true,
        "widgets":[{"id":"a","widgetKind":"Weather","metadata":{"Weather.ViewMode":"Day"}},{"id":"b","widgetKind":"Weather"}]}
        """;
        string a = WeatherInstanceMigration.BuildSnapshot(legacy, "a")!;
        string b = WeatherInstanceMigration.BuildSnapshot(legacy, "b")!;
        var value = JsonSerializer.Deserialize(a, M.WeatherConfigJsonContext.Default.WeatherConfigSnapshot)!;
        Assert.False(value.Settings.WeatherAutoLocation);
        Assert.Equal("上海", value.Settings.WeatherCityName);
        Assert.Equal(31.2, value.Settings.WeatherLatitude);
        Assert.Equal(121.5, value.Settings.WeatherLongitude);
        Assert.Equal("Fahrenheit", value.Settings.WeatherTemperatureUnit);
        Assert.Equal("mph", value.Settings.WeatherWindSpeedUnit);
        Assert.Equal("OpenMeteo", value.Settings.WeatherDataSource);
        Assert.Equal("Week", value.Settings.WeatherDefaultView);
        Assert.Equal("Rich", value.Settings.WeatherSkin);
        Assert.False(value.Settings.WeatherShowForecast);
        Assert.False(value.Settings.WeatherShowSunrise);
        Assert.False(value.Settings.WeatherShowUvIndex);
        Assert.False(value.Settings.WeatherShowPrecipitation);
        Assert.False(value.Settings.WeatherShowHumidity);
        Assert.False(value.Settings.WeatherShowWind);
        Assert.True(value.Settings.WeatherShowPressure);
        Assert.Equal(120, value.Settings.WeatherRefreshIntervalMinutes);
        Assert.Equal("Day", value.Instance.Metadata["Weather.ViewMode"]);
        using var da = JsonDocument.Parse(a);
        using var db = JsonDocument.Parse(b);
        Assert.Equal(17, da.RootElement.GetProperty("settings").EnumerateObject().Count());
        Assert.Equal(da.RootElement.GetProperty("settings").GetRawText(), db.RootElement.GetProperty("settings").GetRawText());
        Assert.DoesNotContain("todoEnabled", a);
        Assert.Null(WeatherInstanceMigration.BuildSnapshot(legacy, "not-present"));
    }

    [Theory]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"weatherShowWind\":true,\"weatherShowWind\":false}")]
    [InlineData("{\"weatherLatitude\":91}")]
    [InlineData("{\"weatherLongitude\":-181}")]
    [InlineData("{\"weatherRefreshIntervalMinutes\":15.5}")]
    [InlineData("{\"weatherTemperatureUnit\":\"Kelvin\"}")]
    [InlineData("{\"weatherShowPressure\":\"true\"}")]
    [InlineData("{\"viewMode\":\"Unknown\"}")]
    public void InvalidPatchIsRejectedBeforeAnyMutation(string json) => Assert.Null(WeatherInstanceMigration.ParsePatch(json));

    [Fact]
    public void PatchDoesNotRequireResubmittingOtherPreferences()
    {
        var patch = WeatherInstanceMigration.ParsePatch("{\"weatherShowPressure\":true,\"requestId\":\"r1\"}");
        Assert.NotNull(patch);
        Assert.Equal(2, patch.Count);
        Assert.True(patch["weatherShowPressure"].GetBoolean());
    }

    [Theory]
    [InlineData("\"Weather\"")]
    [InlineData("2")]
    public void OldMissingFieldsKeepCurrentDefaultSemantics(string kind)
    {
        string json = "{\"widgets\":[{\"id\":\"a\",\"widgetKind\":" + kind + "}]}";
        var value = JsonSerializer.Deserialize(WeatherInstanceMigration.BuildSnapshot(json, "a")!, M.WeatherConfigJsonContext.Default.WeatherConfigSnapshot)!;
        Assert.True(value.Settings.WeatherAutoLocation);
        Assert.Equal(60, value.Settings.WeatherRefreshIntervalMinutes);
        Assert.Equal("MSN", value.Settings.WeatherDataSource);
        Assert.False(value.Settings.WeatherShowPressure);
    }

    [Fact]
    public void CorruptPreferenceCannotBeSilentlyPromotedToAuthoritativeSnapshot()
    {
        Assert.Throws<JsonException>(() => WeatherInstanceMigration.BuildSnapshot("{\"weatherLatitude\":\"bad\",\"widgets\":[{\"id\":\"a\",\"widgetKind\":\"Weather\"}]}", "a"));
    }

    [Fact]
    public void EnvironmentAndCommitReceiptRemainSeparateFromSeventeenPreferences()
    {
        var snapshot = JsonSerializer.Deserialize(WeatherInstanceMigration.BuildSnapshot("{\"widgets\":[{\"id\":\"a\",\"widgetKind\":\"Weather\"}]}", "a", "{\"theme\":\"Dark\"}", "r42", false)!, M.WeatherConfigJsonContext.Default.WeatherConfigSnapshot)!;
        Assert.Equal("r42", snapshot.LastRequestId);
        Assert.False(snapshot.LastWriteSucceeded);
        Assert.Equal("Dark", snapshot.Environment.GetProperty("theme").GetString());
    }

    [Fact]
    public void ViewSelectionKeepsOptimisticValueUntilItsOwnReceiptArrives()
    {
        string? payload = null;
        var context = new P.WeatherSettingsContext(
            new M.WeatherPreferences(),
            json =>
            {
                payload = json;
                return true;
            });
        var config = new M.WeatherInstanceConfig
        {
            Metadata =
            {
                [P.WeatherWidgetViewModeSettings.MetadataKey] = "Week"
            }
        };

        Assert.True(context.UpdateWidget(config));
        Assert.True(context.TryGetPendingWeekView(out bool pendingWeek));
        Assert.True(pendingWeek);
        using JsonDocument request = JsonDocument.Parse(payload!);
        string requestId = request.RootElement.GetProperty("requestId").GetString()!;

        context.ReconcileViewWrite("unrelated-receipt");
        Assert.True(context.TryGetPendingWeekView(out pendingWeek));
        Assert.True(pendingWeek);

        context.ReconcileViewWrite(requestId);
        Assert.False(context.TryGetPendingWeekView(out _));
    }

    [Fact]
    public void RejectedViewSelectionDoesNotCreatePendingState()
    {
        var context = new P.WeatherSettingsContext(
            new M.WeatherPreferences(),
            _ => false);
        var config = new M.WeatherInstanceConfig
        {
            Metadata =
            {
                [P.WeatherWidgetViewModeSettings.MetadataKey] = "Week"
            }
        };

        Assert.False(context.UpdateWidget(config));
        Assert.False(context.TryGetPendingWeekView(out _));
    }
}
