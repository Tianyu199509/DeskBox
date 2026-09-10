using System.Text.Json;
using DeskBox.WeatherPackage.Models;

namespace DeskBox.WeatherPackage.Services;

/// <summary>Package-local snapshot and explicit write port; never a host service locator.</summary>
public sealed class WeatherSettingsContext
{
    public const double DefaultTextSize = 11.5;
    public const double MinTextSize = 10;
    public const double MaxTextSize = 16;
    public const string WeatherTemperatureUnitCelsius = "Celsius";
    public const string WeatherTemperatureUnitFahrenheit = "Fahrenheit";
    public const string WeatherWindSpeedUnitKmh = "kmh";
    public const string WeatherWindSpeedUnitMs = "ms";
    public const string WeatherWindSpeedUnitMph = "mph";
    public const string WeatherDefaultViewToday = "Today";
    public const string WeatherDefaultViewWeek = "Week";
    public const string WeatherSkinStandard = "Standard";
    public const string WeatherSkinRich = "Rich";
    public const string WeatherDataSourceMsn = "MSN";
    public const string WeatherDataSourceOpenMeteo = "OpenMeteo";
    public const int WeatherRefreshMinMinutes = 15;
    public const int WeatherRefreshMaxMinutes = 180;

    public WeatherPreferences Settings { get; private set; }
    public string LastRequestId { get; internal set; } = string.Empty;
    public bool LastWriteSucceeded { get; internal set; } = true;
    private readonly Func<string, bool>? _writePatch;
    private string _pendingViewRequestId = string.Empty;
    private bool? _pendingWeekView;
    public event Action? SettingsChanged;

    public WeatherSettingsContext(WeatherPreferences settings, Func<string, bool>? writePatch = null)
    {
        Normalize(settings);
        settings.TextSize = PackageEnvironment.TextSize;
        Settings = settings;
        _writePatch = writePatch;
    }

    public static double NormalizeTextSize(double value) => double.IsFinite(value)
        ? Math.Clamp(value, MinTextSize, MaxTextSize) : DefaultTextSize;

    public static void Normalize(WeatherPreferences settings)
    {
        settings.WeatherTemperatureUnit = settings.WeatherTemperatureUnit == WeatherTemperatureUnitFahrenheit ? WeatherTemperatureUnitFahrenheit : WeatherTemperatureUnitCelsius;
        settings.WeatherWindSpeedUnit = settings.WeatherWindSpeedUnit is WeatherWindSpeedUnitMs or WeatherWindSpeedUnitMph ? settings.WeatherWindSpeedUnit : WeatherWindSpeedUnitKmh;
        settings.WeatherDefaultView = settings.WeatherDefaultView == WeatherDefaultViewWeek ? WeatherDefaultViewWeek : WeatherDefaultViewToday;
        settings.WeatherSkin = settings.WeatherSkin == WeatherSkinRich ? WeatherSkinRich : WeatherSkinStandard;
        settings.WeatherDataSource = settings.WeatherDataSource == WeatherDataSourceOpenMeteo ? WeatherDataSourceOpenMeteo : WeatherDataSourceMsn;
        settings.WeatherRefreshIntervalMinutes = Math.Clamp(settings.WeatherRefreshIntervalMinutes, WeatherRefreshMinMinutes, WeatherRefreshMaxMinutes);
        settings.WeatherCityName ??= string.Empty;
        if (!double.IsFinite(settings.WeatherLatitude) || settings.WeatherLatitude is < -90 or > 90) settings.WeatherLatitude = 0;
        if (!double.IsFinite(settings.WeatherLongitude) || settings.WeatherLongitude is < -180 or > 180) settings.WeatherLongitude = 0;
    }

    public void Apply(WeatherPreferences settings)
    {
        Normalize(settings);
        settings.TextSize = PackageEnvironment.TextSize;
        Settings = settings;
        SettingsChanged?.Invoke();
    }

    public bool RequestPatch(string json) => _writePatch?.Invoke(json) == true;

    public void SaveDebounced()
    {
        // Geocoding enrichment only; never resubmit a stale full settings object.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("weatherLatitude", Settings.WeatherLatitude);
            writer.WriteNumber("weatherLongitude", Settings.WeatherLongitude);
            writer.WriteEndObject();
        }
        if (!RequestPatch(System.Text.Encoding.UTF8.GetString(buffer.ToArray())))
            PackageLogger.Log("[WeatherPackage] location enrichment was not accepted by host");
    }

    public bool UpdateWidget(WeatherInstanceConfig config, bool notifySubscribers = false)
    {
        if (!config.Metadata.TryGetValue(WeatherWidgetViewModeSettings.MetadataKey, out string? mode))
        {
            return false;
        }
        string requestId = Guid.NewGuid().ToString("N");
        string patch = mode == "Week"
            ? $"{{\"requestId\":\"{requestId}\",\"viewMode\":\"Week\"}}"
            : $"{{\"requestId\":\"{requestId}\",\"viewMode\":\"Day\"}}";
        bool accepted = RequestPatch(patch);
        if (!accepted)
        {
            PackageLogger.Log("[WeatherPackage] instance view selection was not accepted by host");
        }
        else
        {
            _pendingViewRequestId = requestId;
            _pendingWeekView = mode == "Week";
        }
        return accepted;
    }

    internal bool TryGetPendingWeekView(out bool useWeekView)
    {
        useWeekView = _pendingWeekView.GetValueOrDefault();
        return _pendingWeekView.HasValue;
    }

    internal void ReconcileViewWrite(string requestId)
    {
        if (_pendingViewRequestId.Length == 0 ||
            !string.Equals(
                requestId,
                _pendingViewRequestId,
                StringComparison.Ordinal))
        {
            return;
        }

        _pendingViewRequestId = string.Empty;
        _pendingWeekView = null;
    }
}
