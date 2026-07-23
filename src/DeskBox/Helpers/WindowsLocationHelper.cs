using System.Text.Json;
using Windows.Devices.Geolocation;

namespace DeskBox.Helpers;

/// <summary>
/// Uses the Windows Geolocation API (Windows.Devices.Geolocation.Geolocator)
/// to get the current device location.
/// Falls back to multiple IP-based geolocation services.
/// Returns null if all methods fail, allowing the caller to guide the user
/// to manually select a city.
/// </summary>
public static class WindowsLocationHelper
{
    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    /// <summary>
    /// Gets the current location using the Windows Geolocation API.
    /// Returns (latitude, longitude, displayName) or null if all methods fail.
    /// </summary>
    public static async Task<(double Lat, double Lon, string Name)?> GetLocationAsync(
        Services.LocalizationService? localizationService = null)
    {
        // 1. Try Windows Geolocation API (requires user permission)
        try
        {
            var accessStatus = await Geolocator.RequestAccessAsync();
            if (accessStatus == GeolocationAccessStatus.Allowed)
            {
                var geolocator = new Geolocator
                {
                    DesiredAccuracy = PositionAccuracy.High,
                    DesiredAccuracyInMeters = 1000
                };

                var position = await geolocator.GetGeopositionAsync(
                    maximumAge: TimeSpan.FromMinutes(10),
                    timeout: TimeSpan.FromSeconds(10));

                double lat = position.Coordinate.Point.Position.Latitude;
                double lon = position.Coordinate.Point.Position.Longitude;

                bool isEnglish = localizationService?.IsEnglish ?? false;
                string name = isEnglish ? "Current Location" : localizationService?.T("Weather.CurrentLocation");

                App.Log($"[WindowsLocation] Got GPS location lat={lat:F4} lon={lon:F4}");
                return (lat, lon, name);
            }

            App.Log($"[WindowsLocation] Access not allowed: {accessStatus}, trying IP fallback");
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] GPS failed: {ex.Message}, trying IP fallback");
        }

        // 2. Try multiple IP geolocation services
        return await TryMultiSourceIpLocationAsync(localizationService);
    }

    /// <summary>
    /// Tries multiple IP geolocation services in sequence.
    /// Returns the first successful result, or null if all fail.
    /// </summary>
    private static async Task<(double Lat, double Lon, string Name)?> TryMultiSourceIpLocationAsync(
        Services.LocalizationService? localizationService)
    {
        // Source 1: ip-api.com (free, no key, city-level accuracy, works in China)
        var result = await TryIpApiComAsync(localizationService);
        if (result is not null) return result;

        // Source 2: ipapi.co (fallback)
        result = await TryIpApiCoAsync(localizationService);
        if (result is not null) return result;

        // Source 3: ip.sb (lightweight, works in China)
        result = await TryIpSbAsync(localizationService);
        if (result is not null) return result;

        App.Log("[WindowsLocation] All IP location sources failed");
        return null;
    }

    /// <summary>
    /// ip-api.com — free tier, returns city + coordinates.
    /// Supports Chinese city names via language parameter.
    /// </summary>
    private static async Task<(double Lat, double Lon, string Name)?> TryIpApiComAsync(
        Services.LocalizationService? localizationService)
    {
        try
        {
            string lang = localizationService?.ApiLanguageCode ?? "en";
            string json = await s_httpClient.GetStringAsync(
                $"http://ip-api.com/json/?fields=status,lat,lon,city,regionName,country&lang={lang}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var statusElem) &&
                statusElem.GetString() != "success")
            {
                App.Log($"[WindowsLocation] ip-api.com returned status: {statusElem.GetString()}");
                return null;
            }

            if (root.TryGetProperty("lat", out var latElem) &&
                root.TryGetProperty("lon", out var lonElem) &&
                latElem.TryGetDouble(out double lat) &&
                lonElem.TryGetDouble(out double lon))
            {
                string cityName = ExtractCityName(root, localizationService);
                App.Log($"[WindowsLocation] ip-api.com: lat={lat:F4} lon={lon:F4} city={cityName}");
                return (lat, lon, cityName);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] ip-api.com failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// ipapi.co — fallback source.
    /// </summary>
    private static async Task<(double Lat, double Lon, string Name)?> TryIpApiCoAsync(
        Services.LocalizationService? localizationService)
    {
        try
        {
            string json = await s_httpClient.GetStringAsync("https://ipapi.co/json/");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("latitude", out var latElem) &&
                root.TryGetProperty("longitude", out var lonElem) &&
                latElem.TryGetDouble(out double lat) &&
                lonElem.TryGetDouble(out double lon))
            {
                bool isEnglish = localizationService?.IsEnglish ?? false;
                string cityName = string.Empty;

                if (isEnglish && root.TryGetProperty("city", out var cityElem))
                {
                    cityName = cityElem.GetString() ?? string.Empty;
                }
                else if (root.TryGetProperty("region", out var regionElem))
                {
                    cityName = regionElem.GetString() ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(cityName))
                {
                    cityName = isEnglish ? "Current Location" : localizationService?.T("Weather.CurrentLocation") ?? "Current Location";
                }

                App.Log($"[WindowsLocation] ipapi.co: lat={lat:F4} lon={lon:F4} city={cityName}");
                return (lat, lon, cityName);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] ipapi.co failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// ip.sb — lightweight fallback, works well in China.
    /// </summary>
    private static async Task<(double Lat, double Lon, string Name)?> TryIpSbAsync(
        Services.LocalizationService? localizationService)
    {
        try
        {
            string json = await s_httpClient.GetStringAsync("https://api.ip.sb/geoip");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("latitude", out var latElem) &&
                root.TryGetProperty("longitude", out var lonElem) &&
                latElem.TryGetDouble(out double lat) &&
                lonElem.TryGetDouble(out double lon))
            {
                bool isEnglish = localizationService?.IsEnglish ?? false;
                string cityName = string.Empty;

                if (root.TryGetProperty("city", out var cityElem))
                {
                    cityName = cityElem.GetString() ?? string.Empty;
                }
                else if (root.TryGetProperty("region", out var regionElem))
                {
                    cityName = regionElem.GetString() ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(cityName))
                {
                    cityName = isEnglish ? "Current Location" : localizationService?.T("Weather.CurrentLocation") ?? "Current Location";
                }

                App.Log($"[WindowsLocation] ip.sb: lat={lat:F4} lon={lon:F4} city={cityName}");
                return (lat, lon, cityName);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] ip.sb failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Extracts the best available city name from ip-api.com response.
    /// </summary>
    private static string ExtractCityName(JsonElement root, Services.LocalizationService? localizationService)
    {
        // ip-api.com returns city/regionName in the requested language
        if (root.TryGetProperty("city", out var cityElem) &&
            !string.IsNullOrWhiteSpace(cityElem.GetString()))
        {
            return cityElem.GetString()!;
        }

        if (root.TryGetProperty("regionName", out var regionElem) &&
            !string.IsNullOrWhiteSpace(regionElem.GetString()))
        {
            return regionElem.GetString()!;
        }

        return localizationService?.T("Weather.CurrentLocation") ?? "Current Location";
    }
}
