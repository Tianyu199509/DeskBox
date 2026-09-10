using System.Text;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Pre-cutover Weather adapter. The existing host settings remain authoritative.
/// Only committed snapshots are delivered to packages; seventeen feature-wide
/// preferences keep their original scope. No global schema migration is added.
/// </summary>
internal sealed class WeatherInstanceMigration : ILegacyInstanceMigration
{
    internal const string PackageId = "deskbox.weather";
    internal static WeatherInstanceMigration Instance { get; } = new();
    public string DataFileName => "weather-config.json";
    private readonly HashSet<string> _knownInstances = new(StringComparer.Ordinal);
    private SettingsService? _settings;
    private Task _pendingWrites = Task.CompletedTask;
    private bool _syncQueued;
    private bool _committing;
    private string _lastRequestId = "";
    private bool _lastWriteSucceeded = true;

    internal static void Register()
    {
        Plugins.PackageBindingRegistry.Register(new Plugins.OfficialPackageBinding(
            WidgetKind.Weather, PackageId, "weather", Instance));
        BootstrapDevelopmentPackage();
    }

    [System.Diagnostics.Conditional("DESKBOX_NATIVE_DEV_PILOT")]
    private static void BootstrapDevelopmentPackage()
    {
#if DESKBOX_NATIVE_DEV_PILOT
        string? source = Environment.GetEnvironmentVariable("DESKBOX_DEV_NATIVE_WEATHER");
        if (string.IsNullOrWhiteSpace(source)) return;
        var manager = new Plugins.PluginPackageManager(Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "plugins"));
        var result = manager.Install(source, Plugins.PluginPackageVerificationPolicy.Development);
        App.Log(result.Succeeded ? "[WeatherPackage] development package installed through verified pipeline"
            : "[WeatherPackage] development install rejected: " + string.Join("; ", result.Failures));
#endif
    }

    public string? ResolveLegacyContent(string dataDirectory, string instanceId)
    {
        if (_settings is null && App.Current?.SettingsService is { } settings)
        {
            _settings = settings;
            settings.SettingsChanged += SettingsChanged;
            App.Current.ThemeService.AppearanceChanged += SettingsChanged;
        }
        _knownInstances.Add(instanceId);
        foreach (string path in new[] { Path.Combine(dataDirectory, "settings.json"), Path.Combine(dataDirectory, "settings.json.bak") })
        {
            try
            {
                if (!File.Exists(path)) continue;
                string? result = BuildSnapshot(File.ReadAllText(path), instanceId, EnvironmentJson(), _lastRequestId, _lastWriteSucceeded);
                if (result is not null)
                {
                    ImportLegacyCache(dataDirectory);
                    return result;
                }
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { App.Log("[WeatherPackage] legacy candidate rejected: " + error.Message); }
        }
        return null;
    }

    internal static string? BuildSnapshot(string json, string instanceId, string environmentJson = "{}", string requestId = "", bool succeeded = true)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("widgets", out var widgets) || widgets.ValueKind != JsonValueKind.Array) return null;
        JsonElement widget = default;
        foreach (var item in widgets.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id) && id.GetString() == instanceId) { widget = item; break; }
        }
        if (widget.ValueKind != JsonValueKind.Object || !widget.TryGetProperty("widgetKind", out var kind) ||
            !(kind.ValueKind == JsonValueKind.String && string.Equals(kind.GetString(), "Weather", StringComparison.OrdinalIgnoreCase) ||
              kind.ValueKind == JsonValueKind.Number && kind.TryGetInt32(out int numeric) && numeric == (int)WidgetKind.Weather)) return null;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("lastRequestId", requestId);
            writer.WriteBoolean("lastWriteSucceeded", succeeded);
            writer.WritePropertyName("environment");
            using (var environment = JsonDocument.Parse(environmentJson)) environment.RootElement.WriteTo(writer);
            writer.WritePropertyName("settings"); writer.WriteStartObject();
            if (root.TryGetProperty("weatherAutoLocation", out var WeatherAutoLocation)) { if (!IsValidField("weatherAutoLocation", WeatherAutoLocation)) throw new JsonException("Invalid weatherAutoLocation"); writer.WritePropertyName("weatherAutoLocation"); WeatherAutoLocation.WriteTo(writer); }
            else writer.WriteBoolean("weatherAutoLocation", true);
            if (root.TryGetProperty("weatherCityName", out var WeatherCityName)) { if (!IsValidField("weatherCityName", WeatherCityName)) throw new JsonException("Invalid weatherCityName"); writer.WritePropertyName("weatherCityName"); WeatherCityName.WriteTo(writer); }
            else writer.WriteString("weatherCityName", "");
            if (root.TryGetProperty("weatherLatitude", out var WeatherLatitude)) { if (!IsValidField("weatherLatitude", WeatherLatitude)) throw new JsonException("Invalid weatherLatitude"); writer.WritePropertyName("weatherLatitude"); WeatherLatitude.WriteTo(writer); }
            else writer.WriteNumber("weatherLatitude", 0);
            if (root.TryGetProperty("weatherLongitude", out var WeatherLongitude)) { if (!IsValidField("weatherLongitude", WeatherLongitude)) throw new JsonException("Invalid weatherLongitude"); writer.WritePropertyName("weatherLongitude"); WeatherLongitude.WriteTo(writer); }
            else writer.WriteNumber("weatherLongitude", 0);
            if (root.TryGetProperty("weatherTemperatureUnit", out var WeatherTemperatureUnit)) { if (!IsValidField("weatherTemperatureUnit", WeatherTemperatureUnit)) throw new JsonException("Invalid weatherTemperatureUnit"); writer.WritePropertyName("weatherTemperatureUnit"); WeatherTemperatureUnit.WriteTo(writer); }
            else writer.WriteString("weatherTemperatureUnit", "Celsius");
            if (root.TryGetProperty("weatherWindSpeedUnit", out var WeatherWindSpeedUnit)) { if (!IsValidField("weatherWindSpeedUnit", WeatherWindSpeedUnit)) throw new JsonException("Invalid weatherWindSpeedUnit"); writer.WritePropertyName("weatherWindSpeedUnit"); WeatherWindSpeedUnit.WriteTo(writer); }
            else writer.WriteString("weatherWindSpeedUnit", "kmh");
            if (root.TryGetProperty("weatherDataSource", out var WeatherDataSource)) { if (!IsValidField("weatherDataSource", WeatherDataSource)) throw new JsonException("Invalid weatherDataSource"); writer.WritePropertyName("weatherDataSource"); WeatherDataSource.WriteTo(writer); }
            else writer.WriteString("weatherDataSource", "MSN");
            if (root.TryGetProperty("weatherDefaultView", out var WeatherDefaultView)) { if (!IsValidField("weatherDefaultView", WeatherDefaultView)) throw new JsonException("Invalid weatherDefaultView"); writer.WritePropertyName("weatherDefaultView"); WeatherDefaultView.WriteTo(writer); }
            else writer.WriteString("weatherDefaultView", "Today");
            if (root.TryGetProperty("weatherSkin", out var WeatherSkin)) { if (!IsValidField("weatherSkin", WeatherSkin)) throw new JsonException("Invalid weatherSkin"); writer.WritePropertyName("weatherSkin"); WeatherSkin.WriteTo(writer); }
            else writer.WriteString("weatherSkin", "Standard");
            if (root.TryGetProperty("weatherShowForecast", out var WeatherShowForecast)) { if (!IsValidField("weatherShowForecast", WeatherShowForecast)) throw new JsonException("Invalid weatherShowForecast"); writer.WritePropertyName("weatherShowForecast"); WeatherShowForecast.WriteTo(writer); }
            else writer.WriteBoolean("weatherShowForecast", true);
            if (root.TryGetProperty("weatherShowSunrise", out var WeatherShowSunrise)) { if (!IsValidField("weatherShowSunrise", WeatherShowSunrise)) throw new JsonException("Invalid weatherShowSunrise"); writer.WritePropertyName("weatherShowSunrise"); WeatherShowSunrise.WriteTo(writer); }
            else writer.WriteBoolean("weatherShowSunrise", true);
            if (root.TryGetProperty("weatherShowUvIndex", out var WeatherShowUvIndex)) { if (!IsValidField("weatherShowUvIndex", WeatherShowUvIndex)) throw new JsonException("Invalid weatherShowUvIndex"); writer.WritePropertyName("weatherShowUvIndex"); WeatherShowUvIndex.WriteTo(writer); }
            else writer.WriteBoolean("weatherShowUvIndex", true);
            if (root.TryGetProperty("weatherShowPrecipitation", out var WeatherShowPrecipitation)) { if (!IsValidField("weatherShowPrecipitation", WeatherShowPrecipitation)) throw new JsonException("Invalid weatherShowPrecipitation"); writer.WritePropertyName("weatherShowPrecipitation"); WeatherShowPrecipitation.WriteTo(writer); }
            else writer.WriteBoolean("weatherShowPrecipitation", true);
            if (root.TryGetProperty("weatherShowHumidity", out var WeatherShowHumidity)) { if (!IsValidField("weatherShowHumidity", WeatherShowHumidity)) throw new JsonException("Invalid weatherShowHumidity"); writer.WritePropertyName("weatherShowHumidity"); WeatherShowHumidity.WriteTo(writer); }
            else writer.WriteBoolean("weatherShowHumidity", true);
            if (root.TryGetProperty("weatherShowWind", out var WeatherShowWind)) { if (!IsValidField("weatherShowWind", WeatherShowWind)) throw new JsonException("Invalid weatherShowWind"); writer.WritePropertyName("weatherShowWind"); WeatherShowWind.WriteTo(writer); }
            else writer.WriteBoolean("weatherShowWind", true);
            if (root.TryGetProperty("weatherShowPressure", out var WeatherShowPressure)) { if (!IsValidField("weatherShowPressure", WeatherShowPressure)) throw new JsonException("Invalid weatherShowPressure"); writer.WritePropertyName("weatherShowPressure"); WeatherShowPressure.WriteTo(writer); }
            else writer.WriteBoolean("weatherShowPressure", false);
            if (root.TryGetProperty("weatherRefreshIntervalMinutes", out var WeatherRefreshIntervalMinutes)) { if (!IsValidField("weatherRefreshIntervalMinutes", WeatherRefreshIntervalMinutes)) throw new JsonException("Invalid weatherRefreshIntervalMinutes"); writer.WritePropertyName("weatherRefreshIntervalMinutes"); WeatherRefreshIntervalMinutes.WriteTo(writer); }
            else writer.WriteNumber("weatherRefreshIntervalMinutes", 60);
            writer.WriteEndObject();
            writer.WritePropertyName("instance"); writer.WriteStartObject();
            writer.WriteString("id", instanceId);
            writer.WriteString("name", widget.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : "");
            writer.WriteBoolean("isDefaultTitle", !widget.TryGetProperty("isDefaultTitle", out var title) || title.ValueKind != JsonValueKind.False);
            writer.WritePropertyName("metadata"); writer.WriteStartObject();
            if (widget.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty(WeatherWidgetViewModeSettings.MetadataKey, out var view) && view.ValueKind == JsonValueKind.String &&
                view.GetString() is "Day" or "Week") writer.WriteString(WeatherWidgetViewModeSettings.MetadataKey, view.GetString());
            writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static readonly Dictionary<string, string> FieldTypes = new(StringComparer.Ordinal)
    {
        ["weatherAutoLocation"] = "bool",
        ["weatherCityName"] = "string",
        ["weatherLatitude"] = "latitude",
        ["weatherLongitude"] = "longitude",
        ["weatherTemperatureUnit"] = "temperature",
        ["weatherWindSpeedUnit"] = "wind",
        ["weatherDataSource"] = "source",
        ["weatherDefaultView"] = "view",
        ["weatherSkin"] = "skin",
        ["weatherShowForecast"] = "bool",
        ["weatherShowSunrise"] = "bool",
        ["weatherShowUvIndex"] = "bool",
        ["weatherShowPrecipitation"] = "bool",
        ["weatherShowHumidity"] = "bool",
        ["weatherShowWind"] = "bool",
        ["weatherShowPressure"] = "bool",
        ["weatherRefreshIntervalMinutes"] = "interval",
    };

    internal static bool IsValidField(string key, JsonElement value)
    {
        if (key == "viewMode") return value.ValueKind == JsonValueKind.String && value.GetString() is "Day" or "Week";
        if (key == "requestId") return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 64 };
        if (!FieldTypes.TryGetValue(key, out var type)) return false;
        return type switch
        {
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "latitude" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double lat) && double.IsFinite(lat) && lat is >= -90 and <= 90,
            "longitude" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double lon) && double.IsFinite(lon) && lon is >= -180 and <= 180,
            "interval" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int interval) && interval is >= 15 and <= 180,
            "string" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 512,
            "temperature" => value.ValueKind == JsonValueKind.String && value.GetString() is "Celsius" or "Fahrenheit",
            "wind" => value.ValueKind == JsonValueKind.String && value.GetString() is "kmh" or "ms" or "mph",
            "source" => value.ValueKind == JsonValueKind.String && value.GetString() is "MSN" or "OpenMeteo",
            "view" => value.ValueKind == JsonValueKind.String && value.GetString() is "Today" or "Week",
            "skin" => value.ValueKind == JsonValueKind.String && value.GetString() is "Standard" or "Rich",
            _ => false
        };
    }

    internal static Dictionary<string, JsonElement>? ParsePatch(string json)
    {
        if (json.Length > 65536) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var patch = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in doc.RootElement.EnumerateObject())
                if (!IsValidField(field.Name, field.Value) || !patch.TryAdd(field.Name, field.Value.Clone())) return null;
            return patch.Count == 0 ? null : patch;
        }
        catch (JsonException) { return null; }
    }

    public bool TryApplyPatch(string instanceId, string jsonPatch)
    {
        var patch = ParsePatch(jsonPatch);
        SettingsService? settings = _settings ?? App.Current?.SettingsService;
        if (patch is null || settings is null || !settings.Settings.Widgets.Any(w => w.Id == instanceId && w.WidgetKind == WidgetKind.Weather)) return false;
        if (App.UiDispatcherQueue is not { } dispatcher) return false;
        return dispatcher.TryEnqueue(() => { _pendingWrites = CommitAfterAsync(_pendingWrites, settings, instanceId, patch); });
    }

    private async Task CommitAfterAsync(Task previous, SettingsService settings, string instanceId, Dictionary<string, JsonElement> patch)
    {
        try { await previous; } catch { }
        WidgetConfig? widget = settings.Settings.Widgets.FirstOrDefault(w => w.Id == instanceId && w.WidgetKind == WidgetKind.Weather);
        if (widget is null) return;
        string before = SerializeChangedFields(settings.Settings, widget, patch.Keys);
        using var rollback = JsonDocument.Parse(before);
        try
        {
            _committing = true;
            foreach (var (key, value) in patch) ApplyField(settings.Settings, widget, key, value);
            _lastWriteSucceeded = await settings.SaveCheckedAsync();
            if (!_lastWriteSucceeded)
            {
                foreach (var old in rollback.RootElement.EnumerateObject())
                {
                    // Do not revert an intervening user change to a different value.
                    string current = SerializeChangedFields(settings.Settings, widget, [old.Name]);
                    using var currentDoc = JsonDocument.Parse(current);
                    if (currentDoc.RootElement.GetProperty(old.Name).GetRawText() == patch[old.Name].GetRawText())
                        ApplyField(settings.Settings, widget, old.Name, old.Value);
                }
            }
        }
        catch (Exception error) { _lastWriteSucceeded = false; App.Log("[WeatherPackage] save failed: " + error); }
        finally
        {
            _committing = false;
            _lastRequestId = patch.TryGetValue("requestId", out var request) ? request.GetString()! : "";
            await PublishSnapshotsAsync(flush: false);
        }
    }

    private static void ApplyField(AppSettings settings, WidgetConfig widget, string key, JsonElement value)
    {
        switch (key)
        {
            case "weatherAutoLocation": settings.WeatherAutoLocation = value.GetBoolean(); break;
            case "weatherCityName": settings.WeatherCityName = value.GetString()!; break;
            case "weatherLatitude": settings.WeatherLatitude = value.GetDouble(); break;
            case "weatherLongitude": settings.WeatherLongitude = value.GetDouble(); break;
            case "weatherTemperatureUnit": settings.WeatherTemperatureUnit = value.GetString()!; break;
            case "weatherWindSpeedUnit": settings.WeatherWindSpeedUnit = value.GetString()!; break;
            case "weatherDataSource": settings.WeatherDataSource = value.GetString()!; break;
            case "weatherDefaultView": settings.WeatherDefaultView = value.GetString()!; break;
            case "weatherSkin": settings.WeatherSkin = value.GetString()!; break;
            case "weatherShowForecast": settings.WeatherShowForecast = value.GetBoolean(); break;
            case "weatherShowSunrise": settings.WeatherShowSunrise = value.GetBoolean(); break;
            case "weatherShowUvIndex": settings.WeatherShowUvIndex = value.GetBoolean(); break;
            case "weatherShowPrecipitation": settings.WeatherShowPrecipitation = value.GetBoolean(); break;
            case "weatherShowHumidity": settings.WeatherShowHumidity = value.GetBoolean(); break;
            case "weatherShowWind": settings.WeatherShowWind = value.GetBoolean(); break;
            case "weatherShowPressure": settings.WeatherShowPressure = value.GetBoolean(); break;
            case "weatherRefreshIntervalMinutes": settings.WeatherRefreshIntervalMinutes = value.GetInt32(); break;
            case "viewMode": WeatherWidgetViewModeSettings.SetWeekView(widget, value.GetString() == "Week"); break;
        }
    }

    private static string SerializeChangedFields(AppSettings settings, WidgetConfig widget, IEnumerable<string> keys)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (string key in keys) switch (key)
            {
                case "weatherAutoLocation": writer.WriteBoolean(key, settings.WeatherAutoLocation); break;
                case "weatherCityName": writer.WriteString(key, settings.WeatherCityName); break;
                case "weatherLatitude": writer.WriteNumber(key, settings.WeatherLatitude); break;
                case "weatherLongitude": writer.WriteNumber(key, settings.WeatherLongitude); break;
                case "weatherTemperatureUnit": writer.WriteString(key, settings.WeatherTemperatureUnit); break;
                case "weatherWindSpeedUnit": writer.WriteString(key, settings.WeatherWindSpeedUnit); break;
                case "weatherDataSource": writer.WriteString(key, settings.WeatherDataSource); break;
                case "weatherDefaultView": writer.WriteString(key, settings.WeatherDefaultView); break;
                case "weatherSkin": writer.WriteString(key, settings.WeatherSkin); break;
                case "weatherShowForecast": writer.WriteBoolean(key, settings.WeatherShowForecast); break;
                case "weatherShowSunrise": writer.WriteBoolean(key, settings.WeatherShowSunrise); break;
                case "weatherShowUvIndex": writer.WriteBoolean(key, settings.WeatherShowUvIndex); break;
                case "weatherShowPrecipitation": writer.WriteBoolean(key, settings.WeatherShowPrecipitation); break;
                case "weatherShowHumidity": writer.WriteBoolean(key, settings.WeatherShowHumidity); break;
                case "weatherShowWind": writer.WriteBoolean(key, settings.WeatherShowWind); break;
                case "weatherShowPressure": writer.WriteBoolean(key, settings.WeatherShowPressure); break;
                case "weatherRefreshIntervalMinutes": writer.WriteNumber(key, settings.WeatherRefreshIntervalMinutes); break;
                case "viewMode": writer.WriteString(key, WeatherWidgetViewModeSettings.TryGetWeekView(widget, out bool week) && week ? "Week" : "Day"); break;
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void SettingsChanged()
    {
        if (_committing || _syncQueued || _knownInstances.Count == 0) return;
        _syncQueued = true;
        if (App.UiDispatcherQueue?.TryEnqueue(async () => { _syncQueued = false; await PublishSnapshotsAsync(flush: true); }) != true) _syncQueued = false;
    }

    private async Task PublishSnapshotsAsync(bool flush)
    {
        try
        {
            if (_settings is null || (flush && !await _settings.FlushPendingSaveAsync())) return;
            string data = DeskBoxDataPathService.Current.DataDirectory;
            var manager = new Plugins.PluginPackageManager(Path.Combine(data, "plugins"));
            var record = manager.GetInstalled().FirstOrDefault(p => p.PackageId == PackageId);
            if (record is null) return;
            foreach (string id in _knownInstances.ToArray())
            {
                if (!_settings.Settings.Widgets.Any(w => w.Id == id && w.WidgetKind == WidgetKind.Weather)) { _knownInstances.Remove(id); continue; }
                string? json = ResolveLegacyContent(data, id);
                if (json is null) continue;
                string path = Path.Combine(new Plugins.NativePackageIdentity(record.PublisherFingerprint, PackageId).ResolveInstanceDataRoot(data, id), DataFileName);
                await ResilientJsonStore.SaveAsync(path, json);
            }
            Plugins.NativeHostApiBridge.PushConfigChanged();
        }
        catch (Exception error) { App.Log("[WeatherPackage] snapshot delivery failed: " + error); }
    }

    private static string EnvironmentJson()
    {
        var app = App.Current;
        if (app?.SettingsService is not { } settings) return "{}";
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("theme", app.ThemeService.CurrentTheme.ToString());
            var accent = app.ThemeService.GetEffectiveAccentColor();
            writer.WriteString("accent", $"#{accent.A:X2}{accent.R:X2}{accent.G:X2}{accent.B:X2}");
            writer.WriteNumber("textSize", SettingsService.NormalizeTextSize(settings.Settings.TextSize));
            writer.WriteNumber("cornerRadius", WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(settings.Settings.WidgetCornerPreference)));
            writer.WriteBoolean("allowAnimations", WindowsCompatibilityService.ShouldAnimate);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void ImportLegacyCache(string data)
    {
        try
        {
            var manager = new Plugins.PluginPackageManager(Path.Combine(data, "plugins"));
            var record = manager.GetInstalled().FirstOrDefault(p => p.PackageId == PackageId);
            if (record is null) return;
            string root = new Plugins.NativePackageIdentity(record.PublisherFingerprint, PackageId).ResolvePackageDataRoot(data);
            string target = Path.Combine(root, "cache", "weather-cache.json");
            if (File.Exists(target) || File.Exists(target + ".bak")) return;
            foreach (string source in new[] { Path.Combine(data, "weather-cache.json"), Path.Combine(data, "weather-cache.json.bak") })
            {
                if (!File.Exists(source)) continue;
                string content = File.ReadAllText(source);
                WeatherCacheState? cache;
                try { cache = WeatherService.DeserializeCacheState(content); } catch (JsonException) { continue; }
                if (cache?.LastForecast?.IsValid != true && cache?.LastLocation?.IsValid != true) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(source, temporary); File.Move(temporary, target); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                break;
            }
        }
        catch (Exception error) { App.Log("[WeatherPackage] legacy cache import deferred: " + error.Message); }
    }
}
