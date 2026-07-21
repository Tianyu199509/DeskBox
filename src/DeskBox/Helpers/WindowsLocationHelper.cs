using Windows.Devices.Geolocation;

namespace DeskBox.Helpers;

/// <summary>
/// Uses the Windows Geolocation API (Windows.Devices.Geolocation.Geolocator)
/// to get the current device location.
/// Falls back to IP-based geolocation, then to a default location (Beijing)
/// if permission is denied or unavailable.
/// </summary>
public static class WindowsLocationHelper
{
    private static readonly System.Net.Http.HttpClient s_ipLocationClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    /// <summary>
    /// Gets the current location using the Windows Geolocation API.
    /// Returns (latitude, longitude, displayName).
    /// Falls back to IP geolocation, then Beijing if all else fails.
    /// </summary>
    public static async Task<(double Lat, double Lon, string Name)> GetLocationAsync(
        Services.LocalizationService? localizationService = null)
    {
        try
        {
            var accessStatus = await Geolocator.RequestAccessAsync();
            if (accessStatus != GeolocationAccessStatus.Allowed)
            {
                App.Log($"[WindowsLocation] Access not allowed: {accessStatus}, trying IP fallback");
                return await GetIpBasedLocationAsync(localizationService);
            }

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
            string name = isEnglish ? "Current Location" : "\u5F53\u524D\u4F4D\u7F6E";

            App.Log($"[WindowsLocation] Got location lat={lat:F4} lon={lon:F4} name={name}");
            return (lat, lon, name);
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] Failed: {ex.Message}, trying IP fallback");
            return await GetIpBasedLocationAsync(localizationService);
        }
    }

    /// <summary>
    /// IP-based geolocation fallback using a free API.
    /// Returns approximate city-level location.
    /// </summary>
    private static async Task<(double Lat, double Lon, string Name)> GetIpBasedLocationAsync(
        Services.LocalizationService? localizationService)
    {
        try
        {
            string json = await s_ipLocationClient.GetStringAsync("https://ipapi.co/json/");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("latitude", out var latElem) &&
                root.TryGetProperty("longitude", out var lonElem) &&
                latElem.TryGetDouble(out double lat) &&
                lonElem.TryGetDouble(out double lon))
            {
                bool isEnglish = localizationService?.IsEnglish ?? false;
                string cityName = string.Empty;

                if (isEnglish)
                {
                    if (root.TryGetProperty("city", out var cityElem))
                    {
                        cityName = cityElem.GetString() ?? string.Empty;
                    }
                }
                else
                {
                    // ipapi.co doesn't provide localized names; use region as fallback
                    if (root.TryGetProperty("region", out var regionElem))
                    {
                        cityName = regionElem.GetString() ?? string.Empty;
                    }
                }

                if (string.IsNullOrWhiteSpace(cityName))
                {
                    cityName = isEnglish ? "Current Location" : "\u5F53\u524D\u4F4D\u7F6E";
                }

                App.Log($"[WindowsLocation] IP-based location lat={lat:F4} lon={lon:F4} city={cityName}");
                return (lat, lon, cityName);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] IP fallback failed: {ex.Message}");
        }

        return GetDefaultLocation(localizationService);
    }

    private static (double, double, string) GetDefaultLocation(
        Services.LocalizationService? localizationService)
    {
        bool isEnglish = localizationService?.IsEnglish ?? false;
        return (39.9042, 116.4074, isEnglish ? "Beijing" : "\u5317\u4EAC");
    }
}
