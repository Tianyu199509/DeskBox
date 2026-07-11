using Windows.Devices.Geolocation;

namespace DeskBox.Helpers;

/// <summary>
/// Uses the Windows Geolocation API (Windows.Devices.Geolocation.Geolocator)
/// to get the current device location.
/// Falls back to a default location (Beijing) if permission is denied or unavailable.
/// </summary>
public static class WindowsLocationHelper
{
    /// <summary>
    /// Gets the current location using the Windows Geolocation API.
    /// Returns (latitude, longitude, displayName).
    /// Falls back to Beijing if location access is unavailable.
    /// </summary>
    public static async Task<(double Lat, double Lon, string Name)> GetLocationAsync(
        Services.LocalizationService? localizationService = null)
    {
        try
        {
            var accessStatus = await Geolocator.RequestAccessAsync();
            if (accessStatus != GeolocationAccessStatus.Allowed)
            {
                App.Log($"[WindowsLocation] Access not allowed: {accessStatus}");
                return GetDefaultLocation(localizationService);
            }

            var geolocator = new Geolocator
            {
                DesiredAccuracy = PositionAccuracy.Default,
                DesiredAccuracyInMeters = 5000
            };

            var position = await geolocator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(30),
                timeout: TimeSpan.FromSeconds(10));

            double lat = position.Coordinate.Point.Position.Latitude;
            double lon = position.Coordinate.Point.Position.Longitude;

            // Reverse geocoding is not available in all regions (many free APIs
            // are blocked in China). Show a localized "Current Location" label
            // instead of raw coordinates. The user can manually set a city name
            // in Settings → Weather for a personalized display.
            bool isEnglish = localizationService?.IsEnglish ?? false;
            string name = isEnglish ? "Current Location" : "\u5F53\u524D\u4F4D\u7F6E";

            App.Log($"[WindowsLocation] Got location lat={lat:F4} lon={lon:F4} name={name}");
            return (lat, lon, name);
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] Failed: {ex.Message}");
            return GetDefaultLocation(localizationService);
        }
    }

    private static (double, double, string) GetDefaultLocation(
        Services.LocalizationService? localizationService)
    {
        bool isEnglish = localizationService?.IsEnglish ?? false;
        return (39.9042, 116.4074, isEnglish ? "Beijing" : "\u5317\u4EAC");
    }
}
