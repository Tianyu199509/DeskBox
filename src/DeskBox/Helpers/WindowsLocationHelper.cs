using System.Net;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace DeskBox.Helpers;

/// <summary>
/// Uses the Windows Geolocation API (Windows.Devices.Geolocation.Geolocator)
/// to get the current device location.
/// Falls back sequentially to HTTPS IP-based geolocation services.
/// Returns null if all methods fail, allowing the caller to guide the user
/// to manually select a city.
/// </summary>
public static class WindowsLocationHelper
{
    private const string IpSbEndpointUrl = "https://api.ip.sb/geoip";
    private const string IpApiCoEndpointUrl = "https://ipapi.co/json/";

    internal static readonly TimeSpan SuccessfulResultReuseDuration = TimeSpan.FromHours(6);
    internal static readonly TimeSpan FailedResultReuseDuration = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(4);
    private static readonly object s_locationGate = new();
    private static readonly HttpClient s_httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskBox/1.0");
        return client;
    }
    private static Task<(double Lat, double Lon, string Name)?>? s_inFlightLocationTask;
    private static (double Lat, double Lon, string Name)? s_cachedLocation;
    private static DateTimeOffset s_cachedLocationAtUtc;
    private static DateTimeOffset s_lastFailureAtUtc;

    /// <summary>
    /// Gets the current location using the Windows Geolocation API.
    /// Returns (latitude, longitude, displayName) or null if all methods fail.
    /// </summary>
    public static Task<(double Lat, double Lon, string Name)?> GetLocationAsync(
        Services.LocalizationService? localizationService = null,
        bool forceRefresh = false)
    {
        lock (s_locationGate)
        {
            if (!forceRefresh &&
                s_cachedLocation is { } cached &&
                DateTimeOffset.UtcNow - s_cachedLocationAtUtc <= SuccessfulResultReuseDuration)
            {
                App.LogVerbose("[WindowsLocation] Reusing successful in-memory location");
                return Task.FromResult<(double Lat, double Lon, string Name)?>(cached);
            }

            if (!forceRefresh &&
                s_lastFailureAtUtc != default &&
                DateTimeOffset.UtcNow - s_lastFailureAtUtc <= FailedResultReuseDuration)
            {
                App.LogVerbose("[WindowsLocation] Reusing recent failed location result");
                return Task.FromResult<(double Lat, double Lon, string Name)?>(null);
            }

            if (s_inFlightLocationTask is not null)
            {
                return s_inFlightLocationTask;
            }

            var completion = new TaskCompletionSource<
                (double Lat, double Lon, string Name)?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            s_inFlightLocationTask = completion.Task;
            _ = ResolveAndCacheLocationAsync(localizationService, completion);
            return completion.Task;
        }
    }

    private static async Task ResolveAndCacheLocationAsync(
        Services.LocalizationService? localizationService,
        TaskCompletionSource<(double Lat, double Lon, string Name)?> completion)
    {
        try
        {
            (double Lat, double Lon, string Name)? result =
                await ResolveLocationAsync(localizationService).ConfigureAwait(false);
            lock (s_locationGate)
            {
                if (result is not null)
                {
                    s_cachedLocation = result;
                    s_cachedLocationAtUtc = DateTimeOffset.UtcNow;
                    s_lastFailureAtUtc = default;
                }
                else
                {
                    s_lastFailureAtUtc = DateTimeOffset.UtcNow;
                }

                if (ReferenceEquals(s_inFlightLocationTask, completion.Task))
                {
                    s_inFlightLocationTask = null;
                }
            }

            completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            lock (s_locationGate)
            {
                if (ReferenceEquals(s_inFlightLocationTask, completion.Task))
                {
                    s_inFlightLocationTask = null;
                }
            }

            completion.TrySetException(ex);
        }
    }

    private static readonly TimeSpan GeolocationAccessTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GeopositionHardTimeout = TimeSpan.FromSeconds(12);

    internal static async Task<GeolocationAccessStatus?> RequestAccessWithTimeoutAsync(
        Func<Task<GeolocationAccessStatus>> requestAccess,
        TimeSpan? timeout = null)
    {
        TimeSpan effectiveTimeout = timeout ?? GeolocationAccessTimeout;
        Task<GeolocationAccessStatus> accessTask = requestAccess();
        using var delayCts = new CancellationTokenSource();
        Task delayTask = Task.Delay(effectiveTimeout, delayCts.Token);
        Task completed = await Task.WhenAny(accessTask, delayTask).ConfigureAwait(false);
        if (!ReferenceEquals(completed, accessTask))
        {
            // Observe any late exception so a hung WinRT consent/lfsvc task
            // never surfaces as an unobserved TaskException.
            _ = accessTask.ContinueWith(
                static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }

        delayCts.Cancel();
        return await accessTask.ConfigureAwait(false);
    }

    private static async Task<Geoposition?> GetGeopositionWithTimeoutAsync(
        Func<Task<Geoposition>> getPosition,
        TimeSpan? timeout = null)
    {
        TimeSpan effectiveTimeout = timeout ?? GeopositionHardTimeout;
        Task<Geoposition> positionTask = getPosition();
        using var delayCts = new CancellationTokenSource();
        Task delayTask = Task.Delay(effectiveTimeout, delayCts.Token);
        Task completed = await Task.WhenAny(positionTask, delayTask).ConfigureAwait(false);
        if (!ReferenceEquals(completed, positionTask))
        {
            _ = positionTask.ContinueWith(
                static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }

        delayCts.Cancel();
        return await positionTask.ConfigureAwait(false);
    }

    private static async Task<(double Lat, double Lon, string Name)?> ResolveLocationAsync(
        Services.LocalizationService? localizationService)
    {
        // 1. Try Windows Geolocation API (requires user permission). Guard
        // RequestAccessAsync with a bounded timeout so a hung Win10 lfsvc or
        // CoreWindow-less Store consent call cannot poison s_inFlightLocationTask
        // and block HTTPS IP geolocation fallback for the rest of the session.
        try
        {
            GeolocationAccessStatus? accessStatus = await RequestAccessWithTimeoutAsync(
                static () => Geolocator.RequestAccessAsync().AsTask()).ConfigureAwait(false);
            if (accessStatus == GeolocationAccessStatus.Allowed)
            {
                var geolocator = new Geolocator
                {
                    DesiredAccuracy = PositionAccuracy.High,
                    DesiredAccuracyInMeters = 1000
                };

                Geoposition? position = await GetGeopositionWithTimeoutAsync(
                    () => geolocator.GetGeopositionAsync(
                        maximumAge: TimeSpan.FromMinutes(10),
                        timeout: TimeSpan.FromSeconds(10)).AsTask()).ConfigureAwait(false);

                if (position is not null)
                {
                    double lat = position.Coordinate.Point.Position.Latitude;
                    double lon = position.Coordinate.Point.Position.Longitude;

                    if (IsValidCoordinate(lat, lon))
                    {
                        string name = NormalizeLocationName(
                            lat,
                            lon,
                            GetFallbackCurrentLocationName(localizationService),
                            localizationService);

                        App.Log($"[WindowsLocation] Got GPS location lat={lat:F4} lon={lon:F4}");
                        return (lat, lon, name);
                    }
                }

                App.Log("[WindowsLocation] GPS returned invalid or timed-out coordinates, trying IP fallback");
            }
            else
            {
                App.Log($"[WindowsLocation] Access not allowed: {accessStatus}, trying IP fallback");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] GPS failed: {ex.Message}, trying IP fallback");
        }

        // 2. Try sequential HTTPS IP geolocation services
        return await ResolveIpLocationCoreAsync(s_httpClient, localizationService).ConfigureAwait(false);
    }

    /// <summary>
    /// Queries HTTPS IP geolocation providers sequentially so normal lookups
    /// contact only one third-party service and never fall back to cleartext HTTP.
    /// </summary>
    internal static async Task<(double Lat, double Lon, string Name)?> ResolveIpLocationCoreAsync(
        HttpClient httpClient,
        Services.LocalizationService? localizationService = null,
        TimeSpan? providerTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        TimeSpan timeout = providerTimeout ?? DefaultProviderTimeout;

        var primary = await TryIpSbAsync(
            httpClient,
            localizationService,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (primary is not null)
        {
            return primary;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var secondary = await TryIpApiCoAsync(
            httpClient,
            localizationService,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (secondary is not null)
        {
            return secondary;
        }

        App.Log("[WindowsLocation] All HTTPS IP location sources failed");
        return null;
    }

    /// <summary>
    /// ip.sb — lightweight HTTPS primary source, works well globally and in China.
    /// </summary>
    private static Task<(double Lat, double Lon, string Name)?> TryIpSbAsync(
        HttpClient httpClient,
        Services.LocalizationService? localizationService,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        TryJsonGeoIpProviderAsync(
            httpClient,
            providerName: "ip.sb",
            requestUrl: IpSbEndpointUrl,
            localizationService,
            timeout,
            cancellationToken,
            checkErrorProperty: false);

    /// <summary>
    /// ipapi.co — HTTPS fallback source.
    /// </summary>
    private static Task<(double Lat, double Lon, string Name)?> TryIpApiCoAsync(
        HttpClient httpClient,
        Services.LocalizationService? localizationService,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        TryJsonGeoIpProviderAsync(
            httpClient,
            providerName: "ipapi.co",
            requestUrl: IpApiCoEndpointUrl,
            localizationService,
            timeout,
            cancellationToken,
            checkErrorProperty: true);

    private static async Task<(double Lat, double Lon, string Name)?> TryJsonGeoIpProviderAsync(
        HttpClient httpClient,
        string providerName,
        string requestUrl,
        Services.LocalizationService? localizationService,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool checkErrorProperty)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(timeout);

        try
        {
            await using Stream stream = await httpClient.GetStreamAsync(
                requestUrl,
                requestCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: requestCts.Token).ConfigureAwait(false);
            var root = doc.RootElement;

            if (checkErrorProperty &&
                root.TryGetProperty("error", out var errorElem) &&
                errorElem.ValueKind == JsonValueKind.True)
            {
                App.Log($"[WindowsLocation] {providerName} returned error payload");
                return null;
            }

            if (root.TryGetProperty("latitude", out var latElem) &&
                root.TryGetProperty("longitude", out var lonElem) &&
                latElem.TryGetDouble(out double lat) &&
                lonElem.TryGetDouble(out double lon) &&
                IsValidCoordinate(lat, lon))
            {
                string? rawCity = TryGetNonEmptyString(root, "city")
                    ?? TryGetNonEmptyString(root, "region");
                string cityName = NormalizeLocationName(lat, lon, rawCity, localizationService);
                App.Log($"[WindowsLocation] {providerName}: lat={lat:F4} lon={lon:F4} city={cityName}");
                return (lat, lon, cityName);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] {providerName} failed: {ex.Message}");
        }

        return null;
    }

    internal static string NormalizeLocationName(
        double lat,
        double lon,
        string? rawName,
        Services.LocalizationService? localizationService)
    {
        string language = localizationService?.CurrentCultureName
            ?? System.Globalization.CultureInfo.CurrentUICulture.Name;

        try
        {
            string? nearest = Services.CitySearchService.GetNearestCityName(lat, lon, language);
            if (!string.IsNullOrWhiteSpace(nearest))
            {
                return nearest;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] NormalizeLocationName lookup failed: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(rawName))
        {
            return rawName.Trim();
        }

        return GetFallbackCurrentLocationName(localizationService);
    }

    private static string GetFallbackCurrentLocationName(
        Services.LocalizationService? localizationService)
    {
        bool isEnglish = localizationService?.IsEnglish ?? false;
        return isEnglish
            ? "Current Location"
            : localizationService?.T("Weather.CurrentLocation") ?? "Current Location";
    }

    private static bool IsValidCoordinate(double lat, double lon) =>
        double.IsFinite(lat) &&
        double.IsFinite(lon) &&
        lat is >= -90.0 and <= 90.0 &&
        lon is >= -180.0 and <= 180.0;

    private static string? TryGetNonEmptyString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out JsonElement elem) &&
            elem.ValueKind == JsonValueKind.String)
        {
            string? value = elem.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
